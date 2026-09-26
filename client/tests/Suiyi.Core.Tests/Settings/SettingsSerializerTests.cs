using Suiyi.Core.Settings;

namespace Suiyi.Core.Tests.Settings;

public sealed class SettingsSerializerTests
{
    private const string DefaultJson = """
        {
          "schemaVersion": 2,
          "primaryTarget": "zh",
          "secondaryTarget": "en",
          "clipboard": {
            "monitorEnabled": true,
            "debounceMs": 150,
            "minChars": 2,
            "maxChars": 2000
          },
          "hotkey": {
            "translate": "Ctrl+Alt+T",
            "region": "Ctrl+Alt+S"
          },
          "popup": {
            "autoHideSeconds": 8,
            "maxWidth": 480
          },
          "engine": {
            "port": 18780,
            "pythonPath": null,
            "command": null,
            "args": null,
            "modelsDir": null,
            "preload": "zh-en,en-zh",
            "preloadOcr": true
          },
          "glossary": {
            "enabled": true
          },
          "startWithWindows": false
        }

        """;

    private readonly List<string> _warnings = [];

    private SettingsParseResult Parse(string json) => SettingsSerializer.Parse(json, _warnings.Add);

    [Fact]
    public void Default_SerializesIndentedCamelCaseLf()
    {
        Assert.Equal(DefaultJson.ReplaceLineEndings("\n"), SettingsSerializer.Serialize(AppSettings.Default));
    }

    [Fact]
    public void RoundTrip_Default()
    {
        var result = Parse(SettingsSerializer.Serialize(AppSettings.Default));

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void RoundTrip_Custom()
    {
        var custom = AppSettings.Default with
        {
            PrimaryTarget = "ja",
            SecondaryTarget = "en",
            Clipboard = new ClipboardSettings { MonitorEnabled = false, DebounceMs = 300, MinChars = 1, MaxChars = 10000 },
            Hotkey = new HotkeySettings { Translate = string.Empty, Region = "Ctrl+Shift+F2" },
            Popup = new PopupSettings { AutoHideSeconds = 0, MaxWidth = 640 },
            Engine = new EngineSettings
            {
                Port = 19000,
                PythonPath = @"C:\Python311\python.exe",
                Command = @"D:\随译\suiyi-engine.exe",
                Args = ["serve", "--port", "19000"],
                ModelsDir = @"D:\模型",
                Preload = string.Empty,
            },
            StartWithWindows = true,
        };

        var json = SettingsSerializer.Serialize(custom);
        var result = Parse(json);

        Assert.Equal(custom, result.Settings);
        Assert.Empty(_warnings);
        Assert.Contains(@"D:\\随译\\suiyi-engine.exe", json, StringComparison.Ordinal); // 中文不转义
    }

    [Fact]
    public void MissingFields_UseDefaults()
    {
        var result = Parse("""{ "primaryTarget": "en", "clipboard": { "debounceMs": 200 } }""");

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        Assert.Equal(
            AppSettings.Default with { PrimaryTarget = "en", SecondaryTarget = "zh", Clipboard = new ClipboardSettings { DebounceMs = 200 } },
            result.Settings);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void EmptyObject_IsDefault()
    {
        Assert.Equal(AppSettings.Default, Parse("{}").Settings);
    }

    [Fact]
    public void UnknownFields_Ignored()
    {
        var result = Parse("""{ "theme": "dark", "clipboard": { "foo": 1, "maxChars": 3000 }, "future": { "x": [1, 2] } }""");

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        Assert.Equal(3000, result.Settings.Clipboard.MaxChars);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void OutOfRangeField_FallsBackAlone()
    {
        var result = Parse("""{ "primaryTarget": "fr", "secondaryTarget": "ja", "clipboard": { "debounceMs": -1, "maxChars": 5000 } }""");

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        Assert.Equal("zh", result.Settings.PrimaryTarget);
        Assert.Equal("ja", result.Settings.SecondaryTarget);
        Assert.Equal(150, result.Settings.Clipboard.DebounceMs);
        Assert.Equal(5000, result.Settings.Clipboard.MaxChars);
        Assert.Equal(2, _warnings.Count);
    }

    [Fact]
    public void WrongTypes_FallBackPerField()
    {
        var result = Parse("""
            {
              "primaryTarget": 1,
              "startWithWindows": "yes",
              "clipboard": { "monitorEnabled": 0, "debounceMs": "200", "minChars": 1.5, "maxChars": 3000 },
              "hotkey": "Ctrl+Q",
              "popup": { "autoHideSeconds": null },
              "engine": { "port": 99999999999, "pythonPath": 3, "args": "serve", "preload": null }
            }
            """);

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        Assert.Equal(AppSettings.Default with { Clipboard = new ClipboardSettings { MaxChars = 3000 } }, result.Settings);
        Assert.Equal(11, _warnings.Count);
    }

    [Fact]
    public void PreloadOcr_ReadsAndMapsToEngineOptions()
    {
        var result = Parse("""{ "engine": { "preloadOcr": false } }""");

        Assert.False(result.Settings.Engine.PreloadOcr);
        Assert.False(result.Settings.Engine.ToEngineOptions().PreloadOcr);
        Assert.True(AppSettings.Default.Engine.ToEngineOptions().PreloadOcr);
        Assert.NotEqual(AppSettings.Default, result.Settings);
        Assert.Equal(result.Settings, Parse(SettingsSerializer.Serialize(result.Settings)).Settings);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void PreloadOcr_WrongType_FallsBackToOn()
    {
        var result = Parse("""{ "engine": { "preloadOcr": "no" } }""");

        Assert.True(result.Settings.Engine.PreloadOcr);
        Assert.Single(_warnings);
    }

    [Fact]
    public void NullSection_IsDefault()
    {
        var result = Parse("""{ "clipboard": null, "engine": { "pythonPath": null } }""");

        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void CommentsAndTrailingCommas_Allowed()
    {
        var result = Parse("""
            {
              // 手工编辑
              "primaryTarget": "en", /* 目标 */
              "hotkey": { "translate": "Ctrl+Shift+Y", },
            }
            """);

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        Assert.Equal("en", result.Settings.PrimaryTarget);
        Assert.Equal("Ctrl+Shift+Y", result.Settings.Hotkey.Translate);
    }

    [Fact]
    public void PropertyNames_CaseInsensitive()
    {
        Assert.Equal("ja", Parse("""{ "PrimaryTarget": "ja" }""").Settings.PrimaryTarget);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{ \"primaryTarget\": \"zh\" ")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void Corrupt(string json)
    {
        var result = Parse(json);

        Assert.Equal(SettingsParseStatus.Corrupt, result.Status);
        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void HigherSchemaVersion_TooNew()
    {
        var result = Parse("""{ "schemaVersion": 3, "primaryTarget": "en" }""");

        Assert.Equal(SettingsParseStatus.TooNew, result.Status);
        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.Contains("3", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "primaryTarget": "en" }""")]
    [InlineData("""{ "schemaVersion": 0, "primaryTarget": "en" }""")]
    [InlineData("""{ "schemaVersion": "1", "primaryTarget": "en" }""")]
    public void MissingOrOldSchemaVersion_TreatedAsCurrent(string json)
    {
        var result = Parse(json);

        Assert.Equal(SettingsParseStatus.Ok, result.Status);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.Equal("en", result.Settings.PrimaryTarget);
    }

    [Fact]
    public void Arguments_Validated()
    {
        Assert.Throws<ArgumentNullException>(() => SettingsSerializer.Serialize(null!));
        Assert.Throws<ArgumentNullException>(() => SettingsSerializer.Parse(null!));
    }
}
