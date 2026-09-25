using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Clipboard;

namespace Suiyi.Core.Tests.Clipboard;

public class SelfWriteTrackerTests
{
    [Fact]
    public void RecordedSequenceIsIgnored_OthersAreNot()
    {
        var tracker = new SelfWriteTracker(new FakeTimeProvider());
        tracker.RecordOwnWrite(42);

        Assert.True(tracker.ShouldIgnore(42));
        Assert.True(tracker.ShouldIgnore(42));
        Assert.False(tracker.ShouldIgnore(43));
    }

    [Fact]
    public void RemembersOnlyRecentSequences()
    {
        var tracker = new SelfWriteTracker(new FakeTimeProvider());
        for (uint i = 1; i <= 17; i++)
        {
            tracker.RecordOwnWrite(i);
        }

        Assert.False(tracker.ShouldIgnore(1));
        Assert.True(tracker.ShouldIgnore(2));
        Assert.True(tracker.ShouldIgnore(17));
    }

    [Fact]
    public void SuppressNext_IgnoresExactlyOneChangeWithinWindow()
    {
        var clock = new FakeTimeProvider();
        var tracker = new SelfWriteTracker(clock);

        tracker.SuppressNext(TimeSpan.FromMilliseconds(500));
        clock.Advance(TimeSpan.FromMilliseconds(200));

        Assert.True(tracker.ShouldIgnore(7));
        Assert.False(tracker.ShouldIgnore(8));
    }

    [Fact]
    public void SuppressNext_ExpiresAfterWindow()
    {
        var clock = new FakeTimeProvider();
        var tracker = new SelfWriteTracker(clock);

        tracker.SuppressNext(TimeSpan.FromMilliseconds(500));
        clock.Advance(TimeSpan.FromMilliseconds(501));

        Assert.False(tracker.ShouldIgnore(7));
    }

    [Fact]
    public void CancelSuppress_RemovesPendingSuppression()
    {
        var tracker = new SelfWriteTracker(new FakeTimeProvider());
        tracker.SuppressNext(TimeSpan.FromSeconds(1));

        tracker.CancelSuppress();

        Assert.False(tracker.ShouldIgnore(7));
    }

    [Fact]
    public void SuppressNext_RejectsNonPositiveWindow()
    {
        var tracker = new SelfWriteTracker(new FakeTimeProvider());

        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.SuppressNext(TimeSpan.Zero));
    }

    [Fact]
    public void OwnWrite_AlsoConsumesPendingSuppression()
    {
        // 复制译文时同时 SuppressNext + RecordOwnWrite：命中自身序号后不应再吞掉用户下一次真实复制。
        var clock = new FakeTimeProvider();
        var tracker = new SelfWriteTracker(clock);
        tracker.SuppressNext(TimeSpan.FromSeconds(1));
        tracker.RecordOwnWrite(10);

        Assert.True(tracker.ShouldIgnore(10));
        Assert.False(tracker.ShouldIgnore(11));
    }
}
