using Suiyi.Core.Hotkeys;
using Suiyi.Core.Settings;
using Suiyi.Core.Tests.Clipboard;

namespace Suiyi.Core.Tests.Settings;

/// <summary><c>hotkey.region</c>（#55）：默认值、非法值回落、与翻译快捷键冲突、旧版本文件升级。</summary>
public sealed class RegionHotkeySettingsTests
{
    private const string Version1File = """
        {
          "schemaVersion": 1,
          "primaryTarget": "ja",
          "secondaryTarget": "en",
          "clipboard": { "monitorEnabled": false, "debounceMs": 300, "minChars": 3, "maxChars": 1500 },
          "hotkey": { "translate": "Ctrl+Shift+Y" },
          "popup": { "autoHideSeconds": 0, "maxWidth": 600 },
          "engine": { "port": 19000, "preload": "" },
          "startWithWindows": false
        }
        """;

    private readonly List<string> _warnings = [];
    private readonly List<string> _infos = [];

    private SettingsParseResult Parse(string json) => SettingsSerializer.Parse(json, _warnings.Add, _infos.Add);

    private AppSettings Validate(HotkeySettings hotkey) => SettingsRules.Validate(AppSettings.Default with { Hotkey = hotkey }, _warnings.Add);

    [Fact]
    public void Default_IsCtrlAltS_AndParses()
    {
        Assert.Equal("Ctrl+Alt+S", AppSettings.Default.Hotkey.Region);
        Assert.True(HotkeyParser.TryParse(AppSettings.Default.Hotkey.Region, out var gesture, out _));
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'S'), gesture);
        Assert.NotEqual(AppSettings.Default.Hotkey.Translate, AppSettings.Default.Hotkey.Region);
    }

    [Fact]
    public void MissingInFile_UsesDefault()
    {
        var result = Parse("""{ "schemaVersion": 2, "hotkey": { "translate": "Ctrl+Alt+T" } }""");

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        Assert.Equal("Ctrl+Alt+S", result.Settings.Hotkey.Region);
        Assert.Empty(_warnings);
    }

    [Theory]
    [InlineData("Ctrl+Shift+F2")]
    [InlineData("Win+Alt+R")]
    [InlineData("F9")]
    [InlineData("ctrl + alt + x")]
    public void ValidValue_Kept(string value)
    {
        var result = Parse($$"""{ "hotkey": { "region": "{{value}}" } }""");

        Assert.Equal(value, result.Settings.Hotkey.Region);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void EmptyString_DisablesWithoutWarning()
    {
        var result = Parse("""{ "hotkey": { "region": "" } }""");

        Assert.Equal(string.Empty, result.Settings.Hotkey.Region);
        Assert.Empty(_warnings);
    }

    [Theory]
    [InlineData("乱写")]
    [InlineData("Ctrl+Alt")]
    [InlineData("S")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl++S")]
    public void InvalidFormat_FallsBackToDefault(string value)
    {
        var result = Parse($$"""{ "primaryTarget": "en", "hotkey": { "translate": "Ctrl+Alt+T", "region": "{{value}}" } }""");

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        Assert.Equal(HotkeyParser.DefaultRegion, result.Settings.Hotkey.Region);
        Assert.Equal("en", result.Settings.PrimaryTarget);
        var warning = Assert.Single(_warnings);
        Assert.Contains("hotkey.region", warning, StringComparison.Ordinal);
        Assert.Contains(value, warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("true")]
    public void WrongType_FallsBackToDefault(string raw)
    {
        var result = Parse($$"""{ "hotkey": { "region": {{raw}} } }""");

        Assert.Equal(HotkeyParser.DefaultRegion, result.Settings.Hotkey.Region);
        Assert.Single(_warnings);
    }

    [Fact]
    public void Validate_Null_FallsBackToDefault()
    {
        Assert.Equal(HotkeyParser.DefaultRegion, Validate(new HotkeySettings { Region = null! }).Hotkey.Region);
        Assert.Single(_warnings);
    }

    [Theory]
    [InlineData("Ctrl+Alt+T", "Ctrl+Alt+T")]
    [InlineData("Ctrl+Alt+T", "alt+ctrl+t")]
    [InlineData("Ctrl+Alt+S", "Ctrl+Alt+S")]
    public void SameAsTranslate_Disabled(string translate, string region)
    {
        var result = Validate(new HotkeySettings { Translate = translate, Region = region });

        Assert.Equal(string.Empty, result.Hotkey.Region);
        Assert.Equal(translate, result.Hotkey.Translate);
        Assert.Contains("相同", Assert.Single(_warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidRegionFallingBackOntoTranslate_Disabled()
    {
        // 翻译快捷键被用户设成 Ctrl+Alt+S，框选快捷键写错 → 回落默认 Ctrl+Alt+S → 与翻译相同 → 禁用。
        var result = Validate(new HotkeySettings { Translate = "Ctrl+Alt+S", Region = "乱写" });

        Assert.Equal(string.Empty, result.Hotkey.Region);
        Assert.Equal(2, _warnings.Count);
    }

    [Fact]
    public void InvalidTranslate_NotCompared()
    {
        var result = Validate(new HotkeySettings { Translate = "乱写", Region = "Ctrl+Alt+S" });

        Assert.Equal("Ctrl+Alt+S", result.Hotkey.Region);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void BothDisabled_NoConflict()
    {
        var result = Validate(new HotkeySettings { Translate = string.Empty, Region = string.Empty });

        Assert.Equal(string.Empty, result.Hotkey.Region);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Version1File_UpgradesKeepingFields()
    {
        var result = Parse(Version1File);

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        var s = result.Settings;
        Assert.Equal(2, s.SchemaVersion);
        Assert.Equal(HotkeyParser.DefaultRegion, s.Hotkey.Region);
        Assert.Equal("Ctrl+Shift+Y", s.Hotkey.Translate);
        Assert.Equal("ja", s.PrimaryTarget);
        Assert.False(s.Clipboard.MonitorEnabled);
        Assert.Equal(300, s.Clipboard.DebounceMs);
        Assert.Equal(3, s.Clipboard.MinChars);
        Assert.Equal(1500, s.Clipboard.MaxChars);
        Assert.Equal(0, s.Popup.AutoHideSeconds);
        Assert.Equal(19000, s.Engine.Port);
        Assert.Equal(string.Empty, s.Engine.Preload);
        Assert.Empty(_warnings);
        var info = Assert.Single(_infos);
        Assert.Contains("版本 1 升级到 2", info, StringComparison.Ordinal);
        Assert.Contains("hotkey.region", info, StringComparison.Ordinal);
    }

    [Fact]
    public void Version1File_WithTranslateCtrlAltS_RegionDisabled()
    {
        var result = Parse("""{ "schemaVersion": 1, "hotkey": { "translate": "Ctrl+Alt+S" } }""");

        Assert.Equal("Ctrl+Alt+S", result.Settings.Hotkey.Translate);
        Assert.Equal(string.Empty, result.Settings.Hotkey.Region);
        Assert.Single(_warnings);
        Assert.Single(_infos);
    }

    [Fact]
    public void Version1File_SerializesAsVersion2WithRegion()
    {
        var json = SettingsSerializer.Serialize(Parse(Version1File).Settings);

        Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"region\": \"Ctrl+Alt+S\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentVersionFile_NoUpgradeInfo()
    {
        Parse("""{ "schemaVersion": 2 }""");

        Assert.Empty(_infos);
    }

    [Fact]
    public void Store_Version1File_LogsUpgradeAsInfoAndDoesNotRewrite()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(file, Version1File);
        var logger = new RecordingLogger();
        var store = new SettingsStore(file, logger);

        var settings = store.Load();

        Assert.Equal(HotkeyParser.DefaultRegion, settings.Hotkey.Region);
        Assert.Equal(Version1File, File.ReadAllText(file));
        Assert.Contains(logger.Messages, m => m.StartsWith("[Info]", StringComparison.Ordinal) && m.Contains("升级到 2", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("[Warn", StringComparison.Ordinal));

        // 托盘改设置写回时升级为版本 2，并写出 hotkey.region。
        store.Update(s => s.WithPrimaryTarget("en"));
        var written = File.ReadAllText(file);
        Assert.Contains("\"schemaVersion\": 2", written, StringComparison.Ordinal);
        Assert.Contains("\"region\": \"Ctrl+Alt+S\"", written, StringComparison.Ordinal);
        Assert.Contains("\"translate\": \"Ctrl+Shift+Y\"", written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+Alt+T", "alt+ctrl+t", "Ctrl+Alt+T")]
    [InlineData("Ctrl+Alt+S", "Ctrl+Alt+S", "Ctrl+Alt+S")]
    [InlineData("Ctrl+Alt+S", "乱写", "Ctrl+Alt+S")] // 回落默认后冲突
    public void Conflict_ProducesTrayNotice(string translate, string region, string shown)
    {
        var notices = new List<SettingsNotice>();

        SettingsRules.Validate(AppSettings.Default with { Hotkey = new HotkeySettings { Translate = translate, Region = region } }, _warnings.Add, notices.Add);

        var notice = Assert.Single(notices);
        Assert.Equal(SettingsNoticeKind.RegionHotkeyConflict, notice.Kind);
        Assert.Contains($"框选快捷键 {shown} 与翻译快捷键相同", notice.Message, StringComparison.Ordinal);
        Assert.Contains("已禁用", notice.Message, StringComparison.Ordinal);
        Assert.Contains("hotkey.region", notice.Message, StringComparison.Ordinal);
        Assert.Contains("重启", notice.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+Alt+T", "Ctrl+Alt+S")]
    [InlineData("Ctrl+Alt+T", "")] // 用户主动禁用
    [InlineData("", "")]
    [InlineData("乱写", "Ctrl+Alt+S")]
    [InlineData("Ctrl+Alt+T", "乱写")] // 回落默认，不冲突：只有日志
    public void NoConflict_NoNotice(string translate, string region)
    {
        var notices = new List<SettingsNotice>();

        SettingsRules.Validate(AppSettings.Default with { Hotkey = new HotkeySettings { Translate = translate, Region = region } }, _warnings.Add, notices.Add);

        Assert.Empty(notices);
    }

    [Fact]
    public void Parse_ConflictNoticeInResult()
    {
        var result = Parse("""{ "hotkey": { "translate": "Ctrl+Alt+S" } }""");

        Assert.Equal(SettingsNoticeKind.RegionHotkeyConflict, Assert.Single(result.Notices).Kind);
        Assert.Empty(Parse("""{ "hotkey": { "translate": "Ctrl+Alt+T" } }""").Notices);
        Assert.Empty(Parse("not json").Notices);
    }

    [Fact]
    public void Store_ConflictNotice_TakenOncePerLoad()
    {
        using var dir = new TempDirectory();
        var file = dir.File("settings.json");
        File.WriteAllText(file, """{ "schemaVersion": 2, "hotkey": { "translate": "Ctrl+Alt+S", "region": "Ctrl+Alt+S" } }""");
        var store = new SettingsStore(file, new RecordingLogger());

        Assert.Empty(store.TakeLoadNotices());
        store.Load();

        var notice = Assert.Single(store.TakeLoadNotices());
        Assert.Equal(SettingsNoticeKind.RegionHotkeyConflict, notice.Kind);
        Assert.Empty(store.TakeLoadNotices());

        // 托盘写回（含外部修改后的重新读取）不再产生提示。
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        store.Update(s => s.WithPrimaryTarget("en"));
        Assert.Empty(store.TakeLoadNotices());
    }

    [Fact]
    public void Store_NoConflict_NoNotice()
    {
        using var dir = new TempDirectory();
        var file = dir.File("settings.json");
        File.WriteAllText(file, Version1File);
        var store = new SettingsStore(file);

        store.Load();

        Assert.Empty(store.TakeLoadNotices());
    }

    [Fact]
    public void Store_MissingFile_NoNotice()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.File("settings.json"));

        store.Load();

        Assert.Empty(store.TakeLoadNotices());
    }
}
