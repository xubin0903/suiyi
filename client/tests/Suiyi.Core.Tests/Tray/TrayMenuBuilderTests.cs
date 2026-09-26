using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Tray;

public sealed class TrayMenuBuilderTests
{
    [Fact]
    public void Build_HasExpectedStructure()
    {
        var menu = TrayMenuBuilder.Build(new TrayState { Status = TrayStatus.Ready });

        Assert.Equal(
            ["就绪（中文）", "-", "翻译剪贴板", "框选翻译", "暂停监听", "目标语言", "专业术语", "-", "重启翻译服务", "打开设置文件", "打开日志目录", "-", "关于", "退出"],
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlossarySubmenu_ChecksToggleAndShowsStatus(bool enabled)
    {
        var submenu = TrayMenuBuilder.Build(new TrayState { GlossaryEnabled = enabled }).Single(i => i.Text == "专业术语");

        Assert.Equal(
            [TrayCommand.ToggleGlossary, TrayCommand.EditGlossary, TrayCommand.ReloadGlossary],
            submenu.Children.Where(c => c.Command != TrayCommand.None).Select(c => c.Command));
        Assert.Equal(enabled, Find(submenu.Children, TrayCommand.ToggleGlossary).IsChecked);
        Assert.Equal("术语表状态：等待翻译服务就绪", submenu.Children[^1].Text);
        Assert.False(submenu.Children[^1].IsEnabled);
    }

    [Fact]
    public void CommandItems_AreEnabled()
    {
        var menu = TrayMenuBuilder.Build(TrayState.Initial);

        Assert.All(menu.Where(i => i.Command != TrayCommand.None), i => Assert.True(i.IsEnabled));
        Assert.Equal(
            [TrayCommand.TranslateClipboard, TrayCommand.TranslateRegion, TrayCommand.TogglePause, TrayCommand.RestartEngine, TrayCommand.OpenSettings, TrayCommand.OpenLogs, TrayCommand.About, TrayCommand.Exit],
            menu.Where(i => i.Command != TrayCommand.None).Select(i => i.Command));
    }

    [Theory]
    [InlineData(null, "框选翻译")]
    [InlineData("", "框选翻译")]
    [InlineData("Ctrl+Alt+S", "框选翻译（Ctrl+Alt+S）")]
    [InlineData(" Ctrl+Shift+R ", "框选翻译（Ctrl+Shift+R）")]
    public void RegionItem_ShowsActualHotkey(string? hotkey, string expected)
    {
        var item = Find(TrayMenuBuilder.Build(new TrayState { RegionHotkey = hotkey }), TrayCommand.TranslateRegion);

        Assert.Equal(expected, item.Text);
        Assert.True(item.IsEnabled);
    }

    [Fact]
    public void Build_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TrayMenuBuilder.Build(null!));
    }

    internal static TrayMenuItem Find(IReadOnlyList<TrayMenuItem> menu, TrayCommand command) =>
        menu.Concat(menu.SelectMany(i => i.Children)).First(i => i.Command == command);
}
