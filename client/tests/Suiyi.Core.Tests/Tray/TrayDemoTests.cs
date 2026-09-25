using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Tray;

public sealed class TrayDemoTests
{
    [Fact]
    public void Interval_IsTwoSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), TrayDemo.Interval);
    }

    [Fact]
    public void Steps_CycleThroughFourStates()
    {
        var tray = new TrayController();
        var seen = new List<TrayStatus>();
        for (var step = 0; step < 8; step++)
        {
            TrayDemo.ApplyStep(tray, step);
            seen.Add(tray.State.Effective);
        }

        TrayStatus[] cycle = [TrayStatus.Preparing, TrayStatus.Ready, TrayStatus.Paused, TrayStatus.Error];
        Assert.Equal([.. cycle, .. cycle], seen);
    }

    [Fact]
    public void Steps_DoNotRaiseCommandEvents()
    {
        var tray = new TrayController();
        var raised = 0;
        tray.PauseToggled += (_, _) => raised++;
        tray.TargetChanged += (_, _) => raised++;

        for (var step = 0; step < 4; step++)
        {
            TrayDemo.ApplyStep(tray, step);
        }

        Assert.Equal(0, raised);
    }

    [Fact]
    public void NegativeStep_Wraps()
    {
        var tray = new TrayController();
        TrayDemo.ApplyStep(tray, -1);

        Assert.Equal(TrayStatus.Error, tray.State.Effective);
    }

    [Fact]
    public void Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TrayDemo.ApplyStep(null!, 0));
    }
}
