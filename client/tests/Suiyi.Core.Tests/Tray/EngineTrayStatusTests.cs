using Suiyi.Core.Engine;
using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Tray;

public class EngineTrayStatusTests
{
    [Theory]
    [InlineData(EngineState.Stopped, TrayStatus.Preparing)]
    [InlineData(EngineState.Starting, TrayStatus.Preparing)]
    [InlineData(EngineState.Restarting, TrayStatus.Preparing)]
    [InlineData(EngineState.Ready, TrayStatus.Ready)]
    public void Map_NonFailureStates(EngineState state, TrayStatus expected)
    {
        var (status, _) = EngineTrayStatus.Map(new EngineStateChangedEventArgs(state, EngineOwnership.Managed, "说明"));

        Assert.Equal(expected, status);
    }

    [Fact]
    public void Map_Ready_ClearsDetail()
    {
        Assert.Null(EngineTrayStatus.Map(new EngineStateChangedEventArgs(EngineState.Ready, EngineOwnership.External, "复用")).Detail);
    }

    [Fact]
    public void Map_Failed_UsesFailureMessage()
    {
        var failure = new EngineFailure(EngineFailureReason.ExitedBeforeReady, "缺少模型：opus-mt-fr-en");
        var change = new EngineStateChangedEventArgs(EngineState.Failed, EngineOwnership.None, failure.Message, failure);

        Assert.Equal((TrayStatus.Error, "缺少模型：opus-mt-fr-en"), EngineTrayStatus.Map(change));
        Assert.True(EngineTrayStatus.ShouldNotify(change));
    }

    [Theory]
    [InlineData(EngineState.Restarting, true)]
    [InlineData(EngineState.Ready, false)]
    [InlineData(EngineState.Starting, false)]
    public void ShouldNotify(EngineState state, bool expected)
    {
        Assert.Equal(expected, EngineTrayStatus.ShouldNotify(new EngineStateChangedEventArgs(state, EngineOwnership.Managed, "x")));
    }
}
