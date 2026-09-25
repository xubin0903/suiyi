using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Tray;

public sealed class TrayStateTests
{
    [Fact]
    public void Initial_IsPreparingChineseNotPaused()
    {
        var state = TrayState.Initial;

        Assert.Equal(TrayStatus.Preparing, state.Status);
        Assert.False(state.Paused);
        Assert.Equal("zh", state.Target);
        Assert.Equal("随译 · 正在准备…", state.ToolTip);
    }

    [Theory]
    [InlineData("zh", "随译 · 就绪（中文）")]
    [InlineData("en", "随译 · 就绪（English）")]
    [InlineData("ja", "随译 · 就绪（日本語）")]
    public void Ready_ShowsTargetLanguage(string target, string expected)
    {
        var state = new TrayState { Status = TrayStatus.Ready, Target = target };

        Assert.Equal(TrayStatus.Ready, state.Effective);
        Assert.Equal(expected, state.ToolTip);
    }

    [Fact]
    public void ReadyAndPaused_ShowsPaused()
    {
        var state = new TrayState { Status = TrayStatus.Ready, Paused = true };

        Assert.Equal(TrayStatus.Paused, state.Effective);
        Assert.Equal("随译 · 已暂停监听", state.ToolTip);
        Assert.Equal("已暂停监听", state.StatusText);
    }

    [Fact]
    public void ExplicitPausedStatus_ShowsPaused()
    {
        Assert.Equal(TrayStatus.Paused, new TrayState { Status = TrayStatus.Paused }.Effective);
    }

    [Fact]
    public void Error_ShowsReason()
    {
        var state = new TrayState { Status = TrayStatus.Error, Detail = " 连接被拒绝 " };

        Assert.Equal(TrayStatus.Error, state.Effective);
        Assert.Equal("随译 · 翻译服务异常：连接被拒绝", state.ToolTip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Error_WithoutReason(string? detail)
    {
        var state = new TrayState { Status = TrayStatus.Error, Detail = detail };

        Assert.Equal("随译 · 翻译服务异常", state.ToolTip);
    }

    [Theory]
    [InlineData(TrayStatus.Error)]
    [InlineData(TrayStatus.Preparing)]
    public void ErrorAndPreparing_TakePrecedenceOverPaused(TrayStatus status)
    {
        Assert.Equal(status, new TrayState { Status = status, Paused = true }.Effective);
    }

    [Fact]
    public void ToolTip_TruncatedTo127WithEllipsis()
    {
        var state = new TrayState { Status = TrayStatus.Error, Detail = new string('错', 300) };

        Assert.Equal(TrayState.MaxToolTipLength, state.ToolTip.Length);
        Assert.EndsWith("…", state.ToolTip, StringComparison.Ordinal);
        Assert.StartsWith("随译 · 翻译服务异常：错", state.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolTip_FlattensLineBreaks()
    {
        var state = new TrayState { Status = TrayStatus.Error, Detail = "第一行\r\n第二行" };

        Assert.Equal("随译 · 翻译服务异常：第一行 第二行", state.ToolTip);
    }

    [Fact]
    public void UnknownTarget_ShowsCode()
    {
        Assert.Equal("就绪（fr）", new TrayState { Status = TrayStatus.Ready, Target = "fr" }.StatusText);
    }

    [Fact]
    public void Languages_OrderAndLookup()
    {
        Assert.Equal(["zh", "en", "ja"], TrayLanguages.All.Select(l => l.Code));
        Assert.True(TrayLanguages.IsSupported("EN"));
        Assert.False(TrayLanguages.IsSupported("fr"));
        Assert.False(TrayLanguages.IsSupported(null));
        Assert.Equal("日本語", TrayLanguages.GetDisplayName("JA"));
    }
}
