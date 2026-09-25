using Suiyi.Core.Settings;

namespace Suiyi.Core.Tests.Settings;

public sealed class SettingsRulesTests
{
    private readonly List<string> _warnings = [];

    private AppSettings Validate(AppSettings s) => SettingsRules.Validate(s, _warnings.Add);

    [Theory]
    [InlineData("zh", "en", "zh", "en")] // 不同：保持
    [InlineData("zh", "zh", "zh", "en")] // 相同且为 zh：secondary → en
    [InlineData("en", "en", "en", "zh")] // 相同且非 zh：secondary → zh
    [InlineData("ja", "ja", "ja", "zh")]
    [InlineData("en", "ja", "en", "ja")]
    [InlineData(" ZH ", "Zh", "zh", "en")]
    public void ResolveTargets(string primary, string secondary, string expectedPrimary, string expectedSecondary)
    {
        Assert.Equal((expectedPrimary, expectedSecondary), SettingsRules.ResolveTargets(primary, secondary));
    }

    [Fact]
    public void ResolveTargets_Blank_Throws()
    {
        Assert.Throws<ArgumentException>(() => SettingsRules.ResolveTargets("", "en"));
        Assert.Throws<ArgumentException>(() => SettingsRules.ResolveTargets("zh", " "));
    }

    [Fact]
    public void Default_IsValidWithoutWarnings()
    {
        Assert.Equal(AppSettings.Default, Validate(AppSettings.Default));
        Assert.Empty(_warnings);
    }

    [Fact]
    public void UnsupportedPrimary_FallsBackAlone()
    {
        var result = Validate(AppSettings.Default with { PrimaryTarget = "fr", SecondaryTarget = "ja" });

        Assert.Equal("zh", result.PrimaryTarget);
        Assert.Equal("ja", result.SecondaryTarget);
        Assert.Contains(_warnings, w => w.Contains("primaryTarget", StringComparison.Ordinal) && w.Contains("fr", StringComparison.Ordinal));
    }

    [Fact]
    public void Targets_NormalizedAndMadeDistinct()
    {
        var result = Validate(AppSettings.Default with { PrimaryTarget = "EN", SecondaryTarget = "en" });

        Assert.Equal(("en", "zh"), (result.PrimaryTarget, result.SecondaryTarget));
        Assert.Empty(_warnings);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(49)]
    [InlineData(1001)]
    public void DebounceOutOfRange_FallsBack(int value)
    {
        var input = AppSettings.Default with { Clipboard = new ClipboardSettings { DebounceMs = value, MinChars = 5, MonitorEnabled = false } };

        var result = Validate(input);

        Assert.Equal(150, result.Clipboard.DebounceMs);
        Assert.Equal(5, result.Clipboard.MinChars);
        Assert.False(result.Clipboard.MonitorEnabled);
        Assert.Single(_warnings);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(1000)]
    public void DebounceBoundaries_Kept(int value)
    {
        Assert.Equal(value, Validate(AppSettings.Default with { Clipboard = new ClipboardSettings { DebounceMs = value } }).Clipboard.DebounceMs);
    }

    [Fact]
    public void MaxCharsAbove10000_FallsBack()
    {
        var result = Validate(AppSettings.Default with { Clipboard = new ClipboardSettings { MaxChars = 10001 } });

        Assert.Equal(2000, result.Clipboard.MaxChars);
    }

    [Fact]
    public void MaxChars10000_Kept()
    {
        Assert.Equal(10000, Validate(AppSettings.Default with { Clipboard = new ClipboardSettings { MaxChars = 10000 } }).Clipboard.MaxChars);
    }

    [Fact]
    public void MinCharsZero_FallsBack()
    {
        Assert.Equal(2, Validate(AppSettings.Default with { Clipboard = new ClipboardSettings { MinChars = 0 } }).Clipboard.MinChars);
    }

    [Fact]
    public void MinGreaterThanMax_BothFallBack()
    {
        var result = Validate(AppSettings.Default with { Clipboard = new ClipboardSettings { MinChars = 500, MaxChars = 100 } });

        Assert.Equal((2, 2000), (result.Clipboard.MinChars, result.Clipboard.MaxChars));
        Assert.Single(_warnings);
    }

    [Theory]
    [InlineData(-1, 8)]
    [InlineData(0, 0)]
    [InlineData(60, 60)]
    [InlineData(61, 8)]
    public void AutoHideSeconds(int value, int expected)
    {
        Assert.Equal(expected, Validate(AppSettings.Default with { Popup = new PopupSettings { AutoHideSeconds = value } }).Popup.AutoHideSeconds);
    }

    [Theory]
    [InlineData(239, 480)]
    [InlineData(240, 240)]
    [InlineData(1920, 1920)]
    [InlineData(1921, 480)]
    public void PopupMaxWidth(int value, int expected)
    {
        Assert.Equal(expected, Validate(AppSettings.Default with { Popup = new PopupSettings { MaxWidth = value } }).Popup.MaxWidth);
    }

    [Theory]
    [InlineData(0, 18780)]
    [InlineData(1, 1)]
    [InlineData(65535, 65535)]
    [InlineData(65536, 18780)]
    public void EnginePort(int value, int expected)
    {
        Assert.Equal(expected, Validate(AppSettings.Default with { Engine = new EngineSettings { Port = value } }).Engine.Port);
    }

    [Fact]
    public void HotkeyNotFormatValidated_EmptyMeansDisabled()
    {
        Assert.Equal("乱写", Validate(AppSettings.Default with { Hotkey = new HotkeySettings { Translate = "乱写" } }).Hotkey.Translate);
        Assert.Equal(string.Empty, Validate(AppSettings.Default with { Hotkey = new HotkeySettings { Translate = string.Empty } }).Hotkey.Translate);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void NullSections_FallBack()
    {
        var input = AppSettings.Default with { Clipboard = null!, Hotkey = new HotkeySettings { Translate = null! }, Popup = null!, Engine = new EngineSettings { Preload = null! } };

        var result = Validate(input);

        Assert.Equal(AppSettings.Default, result);
        Assert.Equal(4, _warnings.Count);
    }

    [Fact]
    public void EngineBlankStrings_BecomeNull_Trimmed()
    {
        var result = Validate(AppSettings.Default with
        {
            Engine = new EngineSettings { PythonPath = "  ", Command = @" C:\srv\suiyi-engine.exe ", ModelsDir = "", Preload = "" },
        });

        Assert.Null(result.Engine.PythonPath);
        Assert.Equal(@"C:\srv\suiyi-engine.exe", result.Engine.Command);
        Assert.Null(result.Engine.ModelsDir);
        Assert.Equal(string.Empty, result.Engine.Preload); // "" 表示不预加载
    }

    [Fact]
    public void EngineArgsWithNull_FallBack()
    {
        var result = Validate(AppSettings.Default with { Engine = new EngineSettings { Args = ["serve", null!] } });

        Assert.Null(result.Engine.Args);
    }

    [Fact]
    public void SchemaVersion_SetToCurrent()
    {
        Assert.Equal(AppSettings.CurrentSchemaVersion, Validate(AppSettings.Default with { SchemaVersion = 0 }).SchemaVersion);
    }
}
