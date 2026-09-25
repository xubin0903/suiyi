using Suiyi.Core.Hotkeys;
using Suiyi.Core.Popup;
using Suiyi.Core.Settings;

namespace Suiyi.Core.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_MatchIssueTable()
    {
        var s = AppSettings.Default;

        Assert.Equal(2, s.SchemaVersion);
        Assert.Equal("zh", s.PrimaryTarget);
        Assert.Equal("en", s.SecondaryTarget);
        Assert.True(s.Clipboard.MonitorEnabled);
        Assert.Equal(150, s.Clipboard.DebounceMs);
        Assert.Equal(2, s.Clipboard.MinChars);
        Assert.Equal(2000, s.Clipboard.MaxChars);
        Assert.Equal("Ctrl+Alt+T", s.Hotkey.Translate);
        Assert.Equal(HotkeyParser.DefaultTranslate, s.Hotkey.Translate);
        Assert.Equal("Ctrl+Alt+S", s.Hotkey.Region);
        Assert.Equal(HotkeyParser.DefaultRegion, s.Hotkey.Region);
        Assert.Equal(8, s.Popup.AutoHideSeconds);
        Assert.Equal(480, s.Popup.MaxWidth);
        Assert.Equal(18780, s.Engine.Port);
        Assert.Equal(Suiyi.Core.Engine.EngineClient.DefaultPort, s.Engine.Port);
        Assert.Null(s.Engine.PythonPath);
        Assert.Null(s.Engine.Command);
        Assert.Null(s.Engine.Args);
        Assert.Null(s.Engine.ModelsDir);
        Assert.Equal("zh-en,en-zh", s.Engine.Preload);
        Assert.False(s.StartWithWindows);
    }

    [Theory]
    [InlineData("en", "en", "zh")]
    [InlineData("ja", "ja", "en")]
    [InlineData("zh", "zh", "en")]
    public void WithPrimaryTarget_KeepsSecondaryDistinct(string primary, string expectedPrimary, string expectedSecondary)
    {
        var s = AppSettings.Default.WithPrimaryTarget(primary);

        Assert.Equal((expectedPrimary, expectedSecondary), (s.PrimaryTarget, s.SecondaryTarget));
    }

    [Fact]
    public void ToMonitorOptions_Maps()
    {
        var options = new ClipboardSettings { DebounceMs = 300, MinChars = 3, MaxChars = 5000 }.ToMonitorOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(300), options.Debounce);
        Assert.Equal(3, options.Filter.MinChars);
        Assert.Equal(5000, options.Filter.MaxChars);
        options.Validate();
    }

    [Fact]
    public void ToPopupOptions_Maps()
    {
        var options = new PopupSettings { AutoHideSeconds = 0, MaxWidth = 600 }.ToPopupOptions();

        Assert.Equal(0, options.AutoHideSeconds);
        Assert.Equal(600, options.MaxWidth);
        Assert.Equal(new PopupOptions().CursorOffset, options.CursorOffset);
    }

    [Fact]
    public void ToEngineOptions_Maps()
    {
        var options = new EngineSettings
        {
            Port = 19000,
            PythonPath = @"C:\py\python.exe",
            Command = @"D:\suiyi-engine.exe",
            Args = ["--flag"],
            ModelsDir = @"D:\models",
            Preload = string.Empty,
        }.ToEngineOptions();

        Assert.Equal(19000, options.Port);
        Assert.Equal(@"C:\py\python.exe", options.PythonPath);
        Assert.Equal(@"D:\suiyi-engine.exe", options.Command);
        Assert.Equal(["--flag"], options.Args);
        Assert.Equal(@"D:\models", options.ModelsDir);
        Assert.Equal(string.Empty, options.Preload);
    }

    [Fact]
    public void ToEngineOptions_DefaultsMatchEngineOptions()
    {
        var options = AppSettings.Default.Engine.ToEngineOptions();
        var defaults = new Suiyi.Core.Engine.EngineOptions();

        Assert.Equal(defaults.Port, options.Port);
        Assert.Equal(defaults.Preload, options.Preload);
        Assert.Empty(options.Args);
        Assert.Null(options.Command);
        Assert.Null(options.PythonPath);
        Assert.Null(options.ModelsDir);
    }

    [Fact]
    public void Equality_IsStructuralIncludingArgs()
    {
        var a = AppSettings.Default with { Engine = new EngineSettings { Args = ["serve", "--port", "1"] } };
        var b = AppSettings.Default with { Engine = new EngineSettings { Args = ["serve", "--port", "1"] } };
        var c = AppSettings.Default with { Engine = new EngineSettings { Args = ["serve"] } };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, AppSettings.Default);
        Assert.Equal(AppSettings.Default, new AppSettings());
    }
}
