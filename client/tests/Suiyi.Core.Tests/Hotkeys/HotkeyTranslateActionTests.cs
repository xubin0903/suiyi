using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Hotkeys;
using Suiyi.Core.Tests.Clipboard;

namespace Suiyi.Core.Tests.Hotkeys;

public sealed class HotkeyTranslateActionTests : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    private readonly FakeTimeProvider _clock = new();
    private readonly FakeClipboardSource _clipboard = new();
    private readonly FakeKeyboardInput _keyboard = new();
    private readonly RecordingLogger _logger = new();
    private readonly SelfWriteTracker _tracker;
    private readonly ClipboardWriter _writer;
    private readonly HotkeyTranslateAction _action;
    private readonly ClipboardMonitor _monitor;
    private readonly List<ClipboardTextCapturedEventArgs> _captured = [];
    private readonly List<ClipboardTextRejectedEventArgs> _rejected = [];
    private readonly List<ClipboardTextCapturedEventArgs> _monitorCaptured = [];

    public HotkeyTranslateActionTests()
    {
        _tracker = new SelfWriteTracker(_clock);
        _writer = new ClipboardWriter(_clipboard, _tracker);
        _action = new HotkeyTranslateAction(_clipboard, _writer, _keyboard, logger: _logger, timeProvider: _clock);
        _action.TextCaptured += (_, e) => _captured.Add(e);
        _action.Rejected += (_, e) => _rejected.Add(e);

        // 同一套剪贴板上同时运行监听器，验证模拟复制不会被监听重复捕获。
        _monitor = new ClipboardMonitor(_clipboard, _tracker, timeProvider: _clock);
        _monitor.TextCaptured += (_, e) => _monitorCaptured.Add(e);
        _monitor.Start();
    }

    public void Dispose() => _monitor.Dispose();

    /// <summary>
    /// 按 10 ms 推进假时钟直到流程结束，返回推进的总时长。
    /// 上限断言留有余量：若 Task.Delay 的延续没来得及挂上新计时器，多推进一步只会让总时长偏大。
    /// </summary>
    private TimeSpan Run(out HotkeyTranslateResult result)
    {
        var task = _action.ExecuteAsync();
        var advanced = TimeSpan.Zero;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("流程未在预期内结束");
            }

            _clock.Advance(Step);
            advanced += Step;
            Thread.Sleep(1);
        }

        result = task.GetAwaiter().GetResult();
        return advanced;
    }

    /// <summary>让监听器的去抖跑完。</summary>
    private void SettleMonitor() => _clock.Advance(TimeSpan.FromSeconds(1));

    [Fact]
    public void SelectedText_IsCopiedAndCapturedAsHotkey()
    {
        _clipboard.CopyText("原来的剪贴板内容");
        SettleMonitor();
        _monitorCaptured.Clear();
        _keyboard.OnCopy = () => _clipboard.CopyText("  选中的一句中文\r\n");

        Run(out var result);
        SettleMonitor();

        Assert.Equal(HotkeyTranslateStatus.Captured, result.Status);
        Assert.True(result.CopiedSelection);
        var captured = Assert.Single(_captured);
        Assert.Equal("选中的一句中文", captured.Text);
        Assert.Equal(ClipboardTrigger.Hotkey, captured.Trigger);
        Assert.Empty(_monitorCaptured); // 监听器忽略了模拟复制引起的变化
    }

    [Fact]
    public void NoSelection_FallsBackToCurrentClipboard()
    {
        _clipboard.CopyText("剪贴板里已有的句子");
        SettleMonitor();
        _monitorCaptured.Clear();

        var elapsed = Run(out var result);

        Assert.Equal(HotkeyTranslateStatus.Captured, result.Status);
        Assert.False(result.CopiedSelection);
        Assert.Equal("剪贴板里已有的句子", Assert.Single(_captured).Text);
        Assert.Equal(1, _keyboard.CopyCalls);
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public void NoSelection_CancelsSuppression_SoNextUserCopyIsNotSwallowed()
    {
        _clipboard.CopyText("剪贴板里已有的句子");
        SettleMonitor();
        _monitorCaptured.Clear();

        Run(out _);
        _clipboard.CopyText("用户接着复制的新句子");
        SettleMonitor();

        Assert.Equal("用户接着复制的新句子", Assert.Single(_monitorCaptured).Text);
    }

    [Fact]
    public void AfterSelectionCopy_NextUserCopyIsCapturedByMonitor()
    {
        _keyboard.OnCopy = () => _clipboard.CopyText("选中的一句中文");
        Run(out _);
        SettleMonitor();

        _clipboard.CopyText("之后正常复制的句子");
        SettleMonitor();

        Assert.Equal("之后正常复制的句子", Assert.Single(_monitorCaptured).Text);
    }

    [Fact]
    public void SendCopyFailure_StillFallsBack()
    {
        _clipboard.CopyText("剪贴板里已有的句子");
        SettleMonitor();
        _keyboard.SendCopyResult = false;

        Run(out var result);

        Assert.Equal(HotkeyTranslateStatus.Captured, result.Status);
        Assert.False(result.CopiedSelection);
        Assert.Contains(_logger.Messages, m => m.Contains("未被系统接受", StringComparison.Ordinal));
    }

    [Fact]
    public void WaitsForModifierRelease_BeforeCopy()
    {
        _keyboard.ModifiersHeldForPolls = 5;
        _keyboard.OnCopy = () => _clipboard.CopyText("选中的一句中文");

        var elapsed = Run(out var result);

        Assert.Equal(HotkeyTranslateStatus.Captured, result.Status);
        Assert.True(_keyboard.ModifiersReleasedAtCopy);
        Assert.Equal(6, _keyboard.ModifierPolls);
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void ModifierWaitTimesOutAfter500Ms_ThenStillCopies()
    {
        _keyboard.ModifiersHeldForPolls = int.MaxValue;
        _keyboard.OnCopy = () => _clipboard.CopyText("选中的一句中文");

        var elapsed = Run(out var result);

        Assert.Equal(1, _keyboard.CopyCalls);
        Assert.False(_keyboard.ModifiersReleasedAtCopy);
        Assert.Equal(HotkeyTranslateStatus.Captured, result.Status);
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(800));
        Assert.Contains(_logger.Messages, m => m.Contains("修饰键未松开", StringComparison.Ordinal));
    }

    [Fact]
    public void WorksWhileMonitorIsPaused()
    {
        _monitor.Paused = true;
        _keyboard.OnCopy = () => _clipboard.CopyText("暂停监听时选中的句子");

        Run(out var result);
        SettleMonitor();

        Assert.Equal(HotkeyTranslateStatus.Captured, result.Status);
        Assert.Equal("暂停监听时选中的句子", Assert.Single(_captured).Text);
        Assert.Empty(_monitorCaptured);
    }

    [Fact]
    public void AllowsUpTo10000Chars()
    {
        _keyboard.OnCopy = () => _clipboard.CopyText(new string('长', 5000));

        Run(out var result);

        Assert.Equal(HotkeyTranslateStatus.Captured, result.Status);
        Assert.Equal(5000, result.Text!.Length);
    }

    [Fact]
    public void RejectsMoreThan10000Chars()
    {
        _keyboard.OnCopy = () => _clipboard.CopyText(new string('长', 10001));

        Run(out var result);

        Assert.Equal(HotkeyTranslateStatus.Rejected, result.Status);
        Assert.Equal(RejectReason.TooLong, result.Reason);
        var rejected = Assert.Single(_rejected);
        Assert.Equal(ClipboardTrigger.Hotkey, rejected.Trigger);
        Assert.Equal(10001, rejected.Length);
    }

    [Fact]
    public void SameTextTwice_IsNotDeduplicated()
    {
        _clipboard.CopyText("同一句话");
        SettleMonitor();

        Run(out var first);
        Run(out var second);

        Assert.Equal(HotkeyTranslateStatus.Captured, first.Status);
        Assert.Equal(HotkeyTranslateStatus.Captured, second.Status);
        Assert.Equal(2, _captured.Count);
    }

    [Theory]
    [InlineData("12345", RejectReason.NumericLike)]
    [InlineData("https://example.com", RejectReason.Url)]
    public void FilteredText_IsRejected(string text, RejectReason reason)
    {
        _keyboard.OnCopy = () => _clipboard.CopyText(text);

        Run(out var result);

        Assert.Equal(reason, result.Reason);
        Assert.Equal(reason, Assert.Single(_rejected).Reason);
        Assert.Empty(_captured);
    }

    [Fact]
    public void EmptyClipboard_IsRejectedAsNoText()
    {
        Run(out var result);

        Assert.Equal(RejectReason.NoText, result.Reason);
        Assert.Equal(RejectReason.NoText, Assert.Single(_rejected).Reason);
    }

    [Fact]
    public void PrivateClipboard_IsRejected()
    {
        _keyboard.OnCopy = _clipboard.CopyPrivate;

        Run(out var result);

        Assert.Equal(RejectReason.PrivateContent, result.Reason);
    }

    [Fact]
    public void BusyClipboard_RetriesThenReads()
    {
        _keyboard.OnCopy = () =>
        {
            _clipboard.CopyText("被占用后读到的句子");
            _clipboard.BusyReads = 2;
        };

        Run(out var result);

        Assert.Equal(HotkeyTranslateStatus.Captured, result.Status);
        Assert.Equal("被占用后读到的句子", result.Text);
    }

    [Fact]
    public void BusyClipboard_GivesUp()
    {
        _keyboard.OnCopy = () =>
        {
            _clipboard.CopyText("一直被占用");
            _clipboard.BusyReads = 100;
        };

        Run(out var result);

        Assert.Equal(RejectReason.ClipboardBusy, result.Reason);
    }

    [Fact]
    public async Task SecondTriggerWhileRunning_IsIgnored()
    {
        _keyboard.ModifiersHeldForPolls = int.MaxValue;
        var first = _action.ExecuteAsync();

        var second = _action.ExecuteAsync();

        Assert.True(second.IsCompleted);
        Assert.Equal(HotkeyTranslateStatus.AlreadyRunning, (await second).Status);
        while (!first.IsCompleted)
        {
            _clock.Advance(Step);
            Thread.Sleep(1);
        }

        Assert.NotEqual(HotkeyTranslateStatus.AlreadyRunning, (await first).Status);
    }

    [Fact]
    public void Logs_NeverContainText()
    {
        _keyboard.OnCopy = () => _clipboard.CopyText("我的身份证号是一段秘密文字");

        Run(out _);

        Assert.NotEmpty(_logger.Messages);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("身份证", StringComparison.Ordinal));
        Assert.Contains(_logger.Messages, m => m.Contains("捕获选中文本 13 字", StringComparison.Ordinal));
    }

    [Fact]
    public void Options_Validate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _action.Options = _action.Options with { PollInterval = TimeSpan.Zero });
        Assert.Equal(10000, HotkeyTranslateOptions.Default.Filter.MaxChars);
        Assert.Equal(TimeSpan.Zero, HotkeyTranslateOptions.Default.Filter.DuplicateWindow);
    }
}
