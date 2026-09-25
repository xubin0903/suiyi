using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Clipboard;

namespace Suiyi.Core.Tests.Clipboard;

public class DebouncerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    [Fact]
    public void FiresOnceAfterQuietPeriod()
    {
        var clock = new FakeTimeProvider();
        var calls = 0;
        using var debouncer = new Debouncer(Delay, () => calls++, clock);

        debouncer.Signal();
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(0, calls);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RepeatedSignalsRestartTheTimer()
    {
        var clock = new FakeTimeProvider();
        var calls = 0;
        using var debouncer = new Debouncer(Delay, () => calls++, clock);

        for (var i = 0; i < 3; i++)
        {
            debouncer.Signal();
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Cancel_PreventsPendingCallback()
    {
        var clock = new FakeTimeProvider();
        var calls = 0;
        using var debouncer = new Debouncer(Delay, () => calls++, clock);

        debouncer.Signal();
        debouncer.Cancel();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void DelayChange_AppliesToNextSignal()
    {
        var clock = new FakeTimeProvider();
        var calls = 0;
        using var debouncer = new Debouncer(Delay, () => calls++, clock);

        debouncer.Delay = TimeSpan.FromMilliseconds(500);
        debouncer.Signal();
        clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dispose_PreventsCallbacks()
    {
        var clock = new FakeTimeProvider();
        var calls = 0;
        var debouncer = new Debouncer(Delay, () => calls++, clock);

        debouncer.Signal();
        debouncer.Dispose();
        debouncer.Signal();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void RejectsNonPositiveDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Debouncer(TimeSpan.Zero, () => { }, new FakeTimeProvider()));
    }
}
