using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Clipboard;

namespace Suiyi.Core.Tests.Clipboard;

public sealed class ClipboardMonitorTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly FakeTimeProvider _clock = new();
    private readonly FakeClipboardSource _source = new();
    private readonly SelfWriteTracker _tracker;
    private readonly RecordingLogger _logger = new();
    private readonly ClipboardMonitor _monitor;
    private readonly List<ClipboardTextCapturedEventArgs> _captured = [];
    private readonly List<ClipboardTextRejectedEventArgs> _rejected = [];

    public ClipboardMonitorTests()
    {
        _tracker = new SelfWriteTracker(_clock);
        _monitor = new ClipboardMonitor(_source, _tracker, logger: _logger, timeProvider: _clock);
        _monitor.TextCaptured += (_, e) =>
        {
            lock (_captured)
            {
                _captured.Add(e);
            }
        };
        _monitor.TextRejected += (_, e) =>
        {
            lock (_rejected)
            {
                _rejected.Add(e);
            }
        };
        _monitor.Start();
    }

    public void Dispose() => _monitor.Dispose();

    private void CopyAndSettle(string text)
    {
        _source.CopyText(text);
        _clock.Advance(Debounce);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("条件在 5 秒内未满足");
            }

            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Start_StartsListening_StopStopsIt()
    {
        Assert.True(_monitor.IsRunning);
        Assert.True(_source.Listening);

        _monitor.Stop();

        Assert.False(_monitor.IsRunning);
        Assert.False(_source.Listening);
        Assert.Equal(1, _source.StopCount);
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        _monitor.Start();

        Assert.Equal(1, _source.StartCount);
    }

    [Fact]
    public void CopyText_RaisesOneCaptureAfterDebounce()
    {
        _source.CopyText("  今天天气很好\r\n");
        _clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Empty(_captured);

        _clock.Advance(TimeSpan.FromMilliseconds(1));

        var captured = Assert.Single(_captured);
        Assert.Equal("今天天气很好", captured.Text);
        Assert.Equal(ClipboardTrigger.Monitor, captured.Trigger);
        Assert.Empty(_rejected);
    }

    [Theory]
    [InlineData("Hello world")]
    [InlineData("ありがとうございます")]
    public void CopyText_AcceptsEnglishAndJapanese(string text)
    {
        CopyAndSettle(text);

        Assert.Equal(text, Assert.Single(_captured).Text);
    }

    [Fact]
    public void RapidCopies_OnlyLastOneIsProcessed()
    {
        _source.CopyText("第一段文字");
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        _source.CopyText("第二段文字");
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        _source.CopyText("第三段文字");
        _clock.Advance(Debounce);

        Assert.Equal("第三段文字", Assert.Single(_captured).Text);
        Assert.Equal(1, _source.ReadCount);
    }

    [Fact]
    public void NonText_IsRejectedAsNoText()
    {
        _source.CopyNonText();
        _clock.Advance(Debounce);

        Assert.Empty(_captured);
        Assert.Equal(RejectReason.NoText, Assert.Single(_rejected).Reason);
    }

    [Fact]
    public void PrivateContent_IsSkipped()
    {
        _source.CopyPrivate();
        _clock.Advance(Debounce);

        Assert.Empty(_captured);
        var rejected = Assert.Single(_rejected);
        Assert.Equal(RejectReason.PrivateContent, rejected.Reason);
        Assert.Equal(0, rejected.Length);
    }

    [Theory]
    [InlineData("12345", RejectReason.NumericLike)]
    [InlineData("https://example.com", RejectReason.Url)]
    [InlineData("a", RejectReason.TooShort)]
    public void FilteredText_IsRejectedWithReason(string text, RejectReason reason)
    {
        CopyAndSettle(text);

        Assert.Empty(_captured);
        Assert.Equal(reason, Assert.Single(_rejected).Reason);
    }

    [Fact]
    public void TooLongText_ReportsLength()
    {
        CopyAndSettle(new string('字', 2500));

        var rejected = Assert.Single(_rejected);
        Assert.Equal(RejectReason.TooLong, rejected.Reason);
        Assert.Equal(2500, rejected.Length);
    }

    [Fact]
    public void OwnWrite_IsIgnoredSilently()
    {
        var writer = new ClipboardWriter(_source, _tracker, _logger);

        writer.SetText("这是随译写入的译文");
        _clock.Advance(Debounce);

        Assert.Empty(_captured);
        Assert.Empty(_rejected);
        Assert.Equal(0, _source.ReadCount);
        Assert.Contains(_logger.Messages, m => m.Contains("忽略自身写入", StringComparison.Ordinal));
    }

    [Fact]
    public void UserCopyAfterOwnWrite_IsCaptured()
    {
        var writer = new ClipboardWriter(_source, _tracker);
        writer.SetText("这是随译写入的译文");
        _clock.Advance(Debounce);

        CopyAndSettle("用户随后复制的句子");

        Assert.Equal("用户随后复制的句子", Assert.Single(_captured).Text);
    }

    [Fact]
    public void SuppressNext_IgnoresOneChange()
    {
        var writer = new ClipboardWriter(_source, _tracker);
        writer.SuppressNext(TimeSpan.FromMilliseconds(500));

        CopyAndSettle("快捷键模拟复制的文字");
        Assert.Empty(_captured);

        CopyAndSettle("之后正常复制的文字");
        Assert.Equal("之后正常复制的文字", Assert.Single(_captured).Text);
    }

    [Fact]
    public void Paused_RaisesNothing_AndDoesNotReplayOnResume()
    {
        _monitor.Paused = true;
        CopyAndSettle("暂停期间复制的句子");

        Assert.Empty(_captured);
        Assert.Empty(_rejected);
        Assert.Equal(0, _source.ReadCount);
        Assert.True(_source.Listening);

        _monitor.Paused = false;
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(_captured);

        CopyAndSettle("恢复之后复制的句子");
        Assert.Equal("恢复之后复制的句子", Assert.Single(_captured).Text);
    }

    [Fact]
    public void BusyClipboard_RetriesThenSucceeds()
    {
        _source.BusyReads = 2;
        _source.CopyText("剪贴板被占用后读取");
        _clock.Advance(Debounce);
        Assert.Equal(1, _source.ReadCount);

        _clock.Advance(TimeSpan.FromMilliseconds(30));
        WaitUntil(() => _source.ReadCount == 2);
        _clock.Advance(TimeSpan.FromMilliseconds(30));
        WaitUntil(() => _source.ReadCount == 3);
        WaitUntil(() => _captured.Count == 1);

        Assert.Equal("剪贴板被占用后读取", _captured[0].Text);
    }

    [Fact]
    public void BusyClipboard_GivesUpAfterThreeRetries()
    {
        _source.BusyReads = 10;
        _source.CopyText("一直被占用");
        _clock.Advance(Debounce);

        for (var expected = 2; expected <= 4; expected++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(30));
            var count = expected;
            WaitUntil(() => _source.ReadCount == count);
        }

        WaitUntil(() => _rejected.Count == 1);
        Assert.Equal(RejectReason.ClipboardBusy, _rejected[0].Reason);
        Assert.Equal(4, _source.ReadCount);
        Assert.Empty(_captured);
    }

    [Fact]
    public void SameTextWithinTwoSeconds_IsRejectedAsDuplicate()
    {
        CopyAndSettle("重复复制的句子");
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        CopyAndSettle("重复复制的句子");

        Assert.Single(_captured);
        Assert.Equal(RejectReason.Duplicate, Assert.Single(_rejected).Reason);
    }

    [Fact]
    public void SameTextAfterTwoSeconds_IsCapturedAgain()
    {
        CopyAndSettle("重复复制的句子");
        _clock.Advance(TimeSpan.FromSeconds(3));
        CopyAndSettle("重复复制的句子");

        Assert.Equal(2, _captured.Count);
    }

    [Fact]
    public void NotificationWithoutSequenceChange_IsIgnored()
    {
        CopyAndSettle("一句话就好");
        _source.RaiseWithoutChange();
        _clock.Advance(Debounce);

        Assert.Single(_captured);
        Assert.Equal(1, _source.ReadCount);
    }

    [Fact]
    public void AfterStop_ChangesAreNotProcessed()
    {
        _source.CopyText("停止前复制，但未等到去抖结束");
        _monitor.Stop();
        _clock.Advance(Debounce);

        Assert.Empty(_captured);
        Assert.Equal(0, _source.ReadCount);
    }

    [Fact]
    public void OptionsUpdate_ChangesDebounceAndFilter()
    {
        _monitor.Options = _monitor.Options with
        {
            Debounce = TimeSpan.FromMilliseconds(500),
            Filter = ClipboardFilterOptions.Default with { MinChars = 10 },
        };

        _source.CopyText("短句子");
        _clock.Advance(Debounce);
        Assert.Empty(_rejected);

        _clock.Advance(TimeSpan.FromMilliseconds(350));
        Assert.Equal(RejectReason.TooShort, Assert.Single(_rejected).Reason);
    }

    [Fact]
    public void Options_InvalidValueThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _monitor.Options = _monitor.Options with { Debounce = TimeSpan.Zero });
    }

    [Fact]
    public void Logs_NeverContainClipboardText()
    {
        const string Secret = "我的银行卡密码是 abc123xyz";
        CopyAndSettle(Secret);
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        CopyAndSettle(Secret);
        CopyAndSettle(new string('秘', 3000));

        Assert.Single(_captured);
        Assert.NotEmpty(_logger.Messages);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("abc123xyz", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("银行卡", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("秘秘", StringComparison.Ordinal));
        Assert.Contains(_logger.Messages, m => m.Contains("接受 18 字", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_WhileBusy_ReprocessesOnceAfterwards()
    {
        _source.BusyReads = 1;
        _source.CopyText("第一次");
        var first = _monitor.ProcessAsync();
        _source.CopyText("第二次复制的内容");
        await _monitor.ProcessAsync();

        _clock.Advance(TimeSpan.FromMilliseconds(30));
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        WaitUntil(() => _captured.Count == 1);

        // 第一次读取遇到占用，重试时剪贴板已是第二次内容；随后的重跑因序号未变而跳过。
        Assert.Equal("第二次复制的内容", _captured[0].Text);
    }
}
