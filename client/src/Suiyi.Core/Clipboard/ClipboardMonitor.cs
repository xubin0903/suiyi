using System.Globalization;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Clipboard;

/// <summary>
/// 剪贴板文本监听：收到变化 → 去抖 → 忽略自身写入 → 暂停检查 → 读取（占用时重试）→ 过滤 → 发事件。
/// 平台细节由 <see cref="IClipboardSource"/> 提供。
/// </summary>
/// <remarks>
/// <see cref="Start"/> 时捕获当前 <see cref="SynchronizationContext"/>（WPF 中即 UI 线程），
/// 之后的读取与事件都在该上下文上执行；没有上下文时在计时器线程上执行。
/// 日志只记录长度、原因与耗时，不记录剪贴板正文。
/// </remarks>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly IClipboardSource _source;
    private readonly SelfWriteTracker _tracker;
    private readonly ClipboardTextFilter _filter;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Debouncer _debouncer;
    private ClipboardMonitorOptions _options;
    private SynchronizationContext? _context;
    private bool _running;
    private bool _paused;
    private bool _processing;
    private bool _rerun;
    private long _lastChangeTimestamp;
    private uint? _lastSequence;
    private bool _disposed;

    /// <summary>创建监听器。</summary>
    /// <param name="source">剪贴板来源。</param>
    /// <param name="tracker">自身写入跟踪器，必须与 <see cref="ClipboardWriter"/> 共用同一实例。</param>
    /// <param name="options">选项，默认 <see cref="ClipboardMonitorOptions.Default"/>。</param>
    /// <param name="filter">过滤器，默认新建。</param>
    /// <param name="logger">日志。</param>
    /// <param name="timeProvider">时钟，测试时注入。</param>
    public ClipboardMonitor(
        IClipboardSource source,
        SelfWriteTracker tracker,
        ClipboardMonitorOptions? options = null,
        ClipboardTextFilter? filter = null,
        IAppLogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _options = options ?? ClipboardMonitorOptions.Default;
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _filter = filter ?? new ClipboardTextFilter(_timeProvider);
        _logger = logger ?? NullAppLogger.Instance;
        _debouncer = new Debouncer(_options.Debounce, OnDebounced, _timeProvider);
    }

    /// <summary>捕获到值得翻译的文本（<see cref="ClipboardTrigger.Monitor"/>）。</summary>
    public event EventHandler<ClipboardTextCapturedEventArgs>? TextCaptured;

    /// <summary>内容未被接受（仅供日志/调试，不含正文）。暂停与自身写入不触发此事件。</summary>
    public event EventHandler<ClipboardTextRejectedEventArgs>? TextRejected;

    /// <summary>当前选项。设置后立即生效（去抖时长从下一次变化起生效）。</summary>
    public ClipboardMonitorOptions Options
    {
        get
        {
            lock (_gate)
            {
                return _options;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            lock (_gate)
            {
                _options = value;
            }

            _debouncer.Delay = value.Debounce;
        }
    }

    /// <summary>暂停时仍接收系统通知，但不读取剪贴板、不发事件。</summary>
    public bool Paused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }

        set
        {
            bool changed;
            lock (_gate)
            {
                changed = _paused != value;
                _paused = value;
            }

            if (changed)
            {
                _logger.Info(value ? "剪贴板：已暂停监听" : "剪贴板：已恢复监听");
            }
        }
    }

    /// <summary>是否已 <see cref="Start"/>。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>开始监听。在 UI 线程调用，以便读取与事件回到 UI 线程。</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _running = true;
            _context = SynchronizationContext.Current;
            _lastSequence = _source.GetSequenceNumber();
        }

        _source.Changed += OnSourceChanged;
        _source.StartListening();
        _logger.Info("剪贴板：开始监听");
    }

    /// <summary>停止监听，取消尚未触发的去抖。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
        }

        _source.Changed -= OnSourceChanged;
        _source.StopListening();
        _debouncer.Cancel();
        _logger.Info("剪贴板：停止监听");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _debouncer.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// 处理一次（去抖后的）剪贴板变化。处理进行中再次调用时，会在当前处理结束后再处理一次。
    /// </summary>
    internal async Task ProcessAsync()
    {
        lock (_gate)
        {
            if (_processing)
            {
                _rerun = true;
                return;
            }

            _processing = true;
        }

        try
        {
            while (true)
            {
                await ProcessOnceAsync().ConfigureAwait(true);
                lock (_gate)
                {
                    if (!_rerun)
                    {
                        return;
                    }

                    _rerun = false;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _processing = false;
                _rerun = false;
            }
        }
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _lastChangeTimestamp, _timeProvider.GetTimestamp());
        if (IsRunning)
        {
            _debouncer.Signal();
        }
    }

    private void OnDebounced()
    {
        SynchronizationContext? context;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            context = _context;
        }

        if (context is null)
        {
            _ = RunSafelyAsync();
        }
        else
        {
            context.Post(static state => _ = ((ClipboardMonitor)state!).RunSafelyAsync(), this);
        }
    }

    private async Task RunSafelyAsync()
    {
        try
        {
            await ProcessAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 监听回调不能让异常逃逸到计时器或 UI 线程。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.Error("剪贴板：处理变化时出现异常", ex);
        }
    }

    private async Task ProcessOnceAsync()
    {
        ClipboardMonitorOptions options;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            options = _options;
        }

        var started = _timeProvider.GetTimestamp();
        var sequence = _source.GetSequenceNumber();
        lock (_gate)
        {
            if (_lastSequence == sequence)
            {
                return;
            }

            _lastSequence = sequence;
        }

        if (_tracker.ShouldIgnore(sequence))
        {
            _logger.Info("剪贴板：忽略自身写入");
            return;
        }

        if (Paused)
        {
            return;
        }

        var read = _source.TryReadText();
        for (var attempt = 0; read.Status == ClipboardReadStatus.Busy && attempt < options.BusyRetryCount; attempt++)
        {
            await Task.Delay(options.BusyRetryDelay, _timeProvider).ConfigureAwait(true);
            read = _source.TryReadText();
        }

        switch (read.Status)
        {
            case ClipboardReadStatus.Busy:
                _logger.Warn($"剪贴板：被占用，重试 {options.BusyRetryCount} 次后放弃");
                Reject(RejectReason.ClipboardBusy, 0, started);
                return;
            case ClipboardReadStatus.NoText:
                Reject(RejectReason.NoText, 0, started);
                return;
            case ClipboardReadStatus.PrivateContent:
                Reject(RejectReason.PrivateContent, 0, started);
                return;
            case ClipboardReadStatus.Text:
            default:
                break;
        }

        var result = _filter.Evaluate(read.Text, options.Filter);
        if (!result.IsAccepted)
        {
            Reject(result.Reason!.Value, result.Length, started);
            return;
        }

        _logger.Info($"剪贴板：接受 {result.Length} 字，耗时 {FormatMs(started)} ms");
        TextCaptured?.Invoke(this, new ClipboardTextCapturedEventArgs(result.Text!, ClipboardTrigger.Monitor, Interlocked.Read(ref _lastChangeTimestamp)));
    }

    private void Reject(RejectReason reason, int length, long started)
    {
        _logger.Info($"剪贴板：跳过（{reason}，{length} 字），耗时 {FormatMs(started)} ms");
        TextRejected?.Invoke(this, new ClipboardTextRejectedEventArgs(reason, length, ClipboardTrigger.Monitor));
    }

    private string FormatMs(long started) =>
        _timeProvider.GetElapsedTime(started).TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);
}
