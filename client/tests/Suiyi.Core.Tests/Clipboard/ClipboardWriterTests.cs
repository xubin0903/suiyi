using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Clipboard;

namespace Suiyi.Core.Tests.Clipboard;

public class ClipboardWriterTests
{
    [Fact]
    public void SetText_RecordsSequenceAsOwnWrite()
    {
        var source = new FakeClipboardSource();
        var tracker = new SelfWriteTracker(new FakeTimeProvider());
        var writer = new ClipboardWriter(source, tracker);

        Assert.True(writer.SetText("译文"));

        Assert.True(tracker.ShouldIgnore(source.Sequence));
    }

    [Fact]
    public void SetText_Failure_DoesNotRecord()
    {
        var source = new FakeClipboardSource { FailWrites = true };
        var tracker = new SelfWriteTracker(new FakeTimeProvider());
        var writer = new ClipboardWriter(source, tracker);

        Assert.False(writer.SetText("译文"));

        Assert.False(tracker.ShouldIgnore(source.Sequence));
    }

    [Fact]
    public void SetText_LogsLengthOnly()
    {
        var logger = new RecordingLogger();
        var writer = new ClipboardWriter(new FakeClipboardSource(), new SelfWriteTracker(new FakeTimeProvider()), logger);

        writer.SetText("机密译文内容");

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("机密译文内容", StringComparison.Ordinal));
    }

    [Fact]
    public void SuppressNext_DelegatesToTracker()
    {
        var tracker = new SelfWriteTracker(new FakeTimeProvider());
        var writer = new ClipboardWriter(new FakeClipboardSource(), tracker);

        writer.SuppressNext(TimeSpan.FromMilliseconds(300));

        Assert.True(tracker.ShouldIgnore(12345));
    }
}
