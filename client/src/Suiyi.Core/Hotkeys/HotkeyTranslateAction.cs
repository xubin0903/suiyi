using System.Globalization;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Hotkeys;

/// <summary>
/// 快捷键触发后的取词流程：
/// 等待松开修饰键 → <see cref="ClipboardWriter.SuppressNext"/> 让监听忽略 → 记录序号 → 模拟 Ctrl+C
/// → 等待序号变化（变了读新文本，没变回退为当前剪贴板）→ 过滤（上限 10000、不去重）
/// → <see cref="TextCaptured"/>（<see cref="ClipboardTrigger.Hotkey"/>）或 <see cref="Rejected"/>。
/// </summary>
/// <remarks>
/// 不恢复模拟复制前的剪贴板内容。与剪贴板监听是否暂停无关。
/// 应在 UI 线程调用 <see cref="ExecuteAsync"/>（剪贴板读取需要），日志不记录正文。
/// </remarks>
public sealed class HotkeyTranslateAction
{
    private readonly IClipboardSource _clipboard;
    private readonly ClipboardWriter _writer;
    private readonly IKeyboardInput _keyboard;
    private readonly ClipboardTextFilter _filter;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;
    private HotkeyTranslateOptions _options;
    private int _running;

    /// <summary>创建流程。</summary>
    /// <param name="clipboard">剪贴板来源（与监听共用同一实例）。</param>
    /// <param name="writer">剪贴板写入器（与监听共用同一个 <see cref="SelfWriteTracker"/>）。</param>
    /// <param name="keyboard">键盘输入。</param>
    /// <param name="options">选项，默认 <see cref="HotkeyTranslateOptions.Default"/>。</param>
    /// <param name="logger">日志。</param>
    /// <param name="timeProvider">时钟，测试时注入。</param>
    public HotkeyTranslateAction(
        IClipboardSource clipboard,
        ClipboardWriter writer,
        IKeyboardInput keyboard,
        HotkeyTranslateOptions? options = null,
        IAppLogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        _options = options ?? HotkeyTranslateOptions.Default;
        _options.Validate();
        _logger = logger ?? NullAppLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _filter = new ClipboardTextFilter(_timeProvider);
    }

    /// <summary>捕获到文本，<see cref="ClipboardTextCapturedEventArgs.Trigger"/> 为 <see cref="ClipboardTrigger.Hotkey"/>。</summary>
    public event EventHandler<ClipboardTextCapturedEventArgs>? TextCaptured;

    /// <summary>没有可翻译的文本，供浮窗/托盘轻提示（如「剪贴板没有可翻译的文本」）。不含正文。</summary>
    public event EventHandler<ClipboardTextRejectedEventArgs>? Rejected;

    /// <summary>当前选项。</summary>
    public HotkeyTranslateOptions Options
    {
        get => Volatile.Read(ref _options);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            Volatile.Write(ref _options, value);
        }
    }

    /// <summary>执行一次取词。上一次尚未结束时直接返回 <see cref="HotkeyTranslateStatus.AlreadyRunning"/>。</summary>
    public async Task<HotkeyTranslateResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            _logger.Info("快捷键：上一次取词尚未结束，忽略本次");
            return new HotkeyTranslateResult(HotkeyTranslateStatus.AlreadyRunning, null, null, false);
        }

        try
        {
            return await ExecuteCoreAsync(Options, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<HotkeyTranslateResult> ExecuteCoreAsync(HotkeyTranslateOptions options, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();

        // 1. 等用户松开修饰键，否则模拟的 Ctrl+C 会变成 Ctrl+Alt+C。
        var released = await WaitUntilAsync(() => !_keyboard.AreModifierKeysDown(), options.ModifierReleaseTimeout, options.PollInterval, cancellationToken)
            .ConfigureAwait(true);
        if (!released)
        {
            _logger.Warn($"快捷键：{Ms(options.ModifierReleaseTimeout)} ms 内修饰键未松开，仍尝试复制");
        }

        // 2. 让监听忽略这次变化，并记录当前序号。
        _writer.SuppressNext(options.SuppressWindow);
        var before = _clipboard.GetSequenceNumber();

        // 3. 模拟 Ctrl+C。
        if (!_keyboard.SendCopy())
        {
            _logger.Warn("快捷键：模拟 Ctrl+C 未被系统接受");
        }

        // 4. 等序号变化。
        var copied = await WaitUntilAsync(() => _clipboard.GetSequenceNumber() != before, options.CopyTimeout, options.PollInterval, cancellationToken)
            .ConfigureAwait(true);
        if (!copied)
        {
            // 没有选中文本、目标程序不响应，或目标窗口权限更高（UIPI 拦截 SendInput）。
            _writer.CancelSuppress();
            _logger.Info("快捷键：模拟复制未改变剪贴板（可能无选中文本或目标窗口以管理员权限运行），改为翻译当前剪贴板");
        }

        // 5. 读取并过滤。
        var read = _clipboard.TryReadText();
        for (var attempt = 0; read.Status == ClipboardReadStatus.Busy && attempt < options.BusyRetryCount; attempt++)
        {
            await Task.Delay(options.BusyRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(true);
            read = _clipboard.TryReadText();
        }

        var source = copied ? "选中文本" : "当前剪贴板";
        RejectReason? readReason = read.Status switch
        {
            ClipboardReadStatus.Busy => RejectReason.ClipboardBusy,
            ClipboardReadStatus.NoText => RejectReason.NoText,
            ClipboardReadStatus.PrivateContent => RejectReason.PrivateContent,
            _ => null,
        };
        if (readReason is { } r)
        {
            return Reject(r, 0, copied, source, started);
        }

        var result = _filter.Evaluate(read.Text, options.Filter);
        if (!result.IsAccepted)
        {
            return Reject(result.Reason!.Value, result.Length, copied, source, started);
        }

        _logger.Info($"快捷键：捕获{source} {result.Length} 字，耗时 {Elapsed(started)} ms");
        TextCaptured?.Invoke(this, new ClipboardTextCapturedEventArgs(result.Text!, ClipboardTrigger.Hotkey));
        return new HotkeyTranslateResult(HotkeyTranslateStatus.Captured, result.Text, null, copied);
    }

    private HotkeyTranslateResult Reject(RejectReason reason, int length, bool copied, string source, long started)
    {
        _logger.Info($"快捷键：{source}不可翻译（{reason}，{length} 字），耗时 {Elapsed(started)} ms");
        Rejected?.Invoke(this, new ClipboardTextRejectedEventArgs(reason, length, ClipboardTrigger.Hotkey));
        return new HotkeyTranslateResult(HotkeyTranslateStatus.Rejected, null, reason, copied);
    }

    /// <summary>每隔 <paramref name="interval"/> 检查一次条件，最长 <paramref name="timeout"/>。</summary>
    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, TimeSpan interval, CancellationToken cancellationToken)
    {
        var start = _timeProvider.GetTimestamp();
        while (true)
        {
            if (condition())
            {
                return true;
            }

            if (_timeProvider.GetElapsedTime(start) >= timeout)
            {
                return false;
            }

            await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(true);
        }
    }

    private string Elapsed(long started) => Ms(_timeProvider.GetElapsedTime(started));

    private static string Ms(TimeSpan span) => span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
}
