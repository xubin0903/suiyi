using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Tray;

public sealed class TrayMenuBuilderTests
{
    [Fact]
    public void Build_HasExpectedStructure()
    {
        var menu = TrayMenuBuilder.Build(new TrayState { Status = TrayStatus.Ready });

        Assert.Equal(
            ["就绪（中文）", "-", "翻译剪贴板", "暂停监听", "目标语言", "-", "重启翻译服务", "打开设置文件", "打开日志目录", "-", "关于", "退出"],
            menu.Select(i => i.IsSeparator ? "-" : i.Text));
    }

    [Fact]
    public void StatusLine_IsDisabledAndFollowsState()
    {
        var menu = TrayMenuBuilder.Build(new TrayState { Status = TrayStatus.Error, Detail = "未运行" });

        Assert.False(menu[0].IsEnabled);
        Assert.Equal(TrayCommand.None, menu[0].Command);
        Assert.Equal("翻译服务异常：未运行", menu[0].Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PauseItem_CheckedWhenPaused(bool paused)
    {
        var item = Find(TrayMenuBuilder.Build(new TrayState { Paused = paused }), TrayCommand.TogglePause);

        Assert.Equal(paused, item.IsChecked);
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("en")]
    [InlineData("ja")]
    public void TargetSubmenu_ChecksOnlyCurrent(string target)
    {
        var submenu = TrayMenuBuilder.Build(new TrayState { Target = target }).Single(i => i.Text == "目标语言");

        Assert.Equal(["中文", "English", "日本語"], submenu.Children.Select(c => c.Text));
        Assert.All(submenu.Children, c => Assert.Equal(TrayCommand.SetTarget, c.Command));
        Assert.Equal(target, Assert.Single(submenu.Children, c => c.IsChecked).Argument);
    }

    [Fact]
    public void CommandItems_AreEnabled()
    {
        var menu = TrayMenuBuilder.Build(TrayState.Initial);

        Assert.All(menu.Where(i => i.Command != TrayCommand.None), i => Assert.True(i.IsEnabled));
        Assert.Equal(
            [TrayCommand.TranslateClipboard, TrayCommand.TogglePause, TrayCommand.RestartEngine, TrayCommand.OpenSettings, TrayCommand.OpenLogs, TrayCommand.About, TrayCommand.Exit],
            menu.Where(i => i.Command != TrayCommand.None).Select(i => i.Command));
    }

    [Fact]
    public void Build_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TrayMenuBuilder.Build(null!));
    }

    internal static TrayMenuItem Find(IReadOnlyList<TrayMenuItem> menu, TrayCommand command) =>
        menu.Concat(menu.SelectMany(i => i.Children)).First(i => i.Command == command);
}
