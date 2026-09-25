using System.Text.Encodings.Web;
using System.Text.Json;

namespace Suiyi.Core.Settings;

/// <summary>解析结果类别。</summary>
public enum SettingsParseStatus
{
    /// <summary>解析成功（可能有字段回落默认值）。</summary>
    Ok,

    /// <summary>不是合法 JSON 对象。</summary>
    Corrupt,

    /// <summary><c>schemaVersion</c> 高于当前版本（新版客户端写的）。</summary>
    TooNew,
}

/// <summary>解析结果。</summary>
/// <param name="Status">类别。</param>
/// <param name="Settings">解析出的设置；非 <see cref="SettingsParseStatus.Ok"/> 时为默认值。</param>
/// <param name="Error">失败原因。</param>
public sealed record SettingsParseResult(SettingsParseStatus Status, AppSettings Settings, string? Error = null);

/// <summary>
/// <c>settings.json</c> 序列化：写出 UTF-8（无 BOM）、缩进、camelCase；
/// 读取宽松：允许注释与尾逗号，未知字段忽略，缺失字段取默认，类型不对或越界的字段单独回落默认值。
/// </summary>
public static class SettingsSerializer
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,

        // 路径、中文原样写出，方便手工编辑。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>序列化为 JSON 文本（结尾带换行）。</summary>
    public static string Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, WriteOptions).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>解析 JSON 文本。字段级问题通过 <paramref name="warn"/> 报告。</summary>
    public static SettingsParseResult Parse(string json, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        warn ??= _ => { };
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            return new SettingsParseResult(SettingsParseStatus.Corrupt, AppSettings.Default, $"JSON 格式错误：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SettingsParseResult(SettingsParseStatus.Corrupt, AppSettings.Default, "根节点不是 JSON 对象");
            }

            var reader = new FieldReader(warn);
            var version = reader.Int(root, "schemaVersion", AppSettings.CurrentSchemaVersion);
            if (version > AppSettings.CurrentSchemaVersion)
            {
                return new SettingsParseResult(
                    SettingsParseStatus.TooNew,
                    AppSettings.Default,
                    $"设置文件版本 {version} 高于当前支持的 {AppSettings.CurrentSchemaVersion}");
            }

            var d = AppSettings.Default;
            var clipboard = reader.Object(root, "clipboard");
            var hotkey = reader.Object(root, "hotkey");
            var popup = reader.Object(root, "popup");
            var engine = reader.Object(root, "engine");

            var settings = new AppSettings
            {
                SchemaVersion = version,
                PrimaryTarget = reader.String(root, "primaryTarget", d.PrimaryTarget)!,
                SecondaryTarget = reader.String(root, "secondaryTarget", d.SecondaryTarget)!,
                StartWithWindows = reader.Bool(root, "startWithWindows", d.StartWithWindows),
                Clipboard = new ClipboardSettings
                {
                    MonitorEnabled = reader.Bool(clipboard, "monitorEnabled", d.Clipboard.MonitorEnabled, "clipboard."),
                    DebounceMs = reader.Int(clipboard, "debounceMs", d.Clipboard.DebounceMs, "clipboard."),
                    MinChars = reader.Int(clipboard, "minChars", d.Clipboard.MinChars, "clipboard."),
                    MaxChars = reader.Int(clipboard, "maxChars", d.Clipboard.MaxChars, "clipboard."),
                },
                Hotkey = new HotkeySettings
                {
                    Translate = reader.String(hotkey, "translate", d.Hotkey.Translate, "hotkey.")!,
                },
                Popup = new PopupSettings
                {
                    AutoHideSeconds = reader.Int(popup, "autoHideSeconds", d.Popup.AutoHideSeconds, "popup."),
                    MaxWidth = reader.Int(popup, "maxWidth", d.Popup.MaxWidth, "popup."),
                },
                Engine = new EngineSettings
                {
                    Port = reader.Int(engine, "port", d.Engine.Port, "engine."),
                    PythonPath = reader.String(engine, "pythonPath", null, "engine.", nullable: true),
                    Command = reader.String(engine, "command", null, "engine.", nullable: true),
                    Args = reader.StringArray(engine, "args", "engine."),
                    ModelsDir = reader.String(engine, "modelsDir", null, "engine.", nullable: true),
                    Preload = reader.String(engine, "preload", d.Engine.Preload, "engine.")!,
                },
            };

            return new SettingsParseResult(SettingsParseStatus.Ok, SettingsRules.Validate(settings, warn));
        }
    }

    /// <summary>按字段读取，类型不对时报告并返回默认值。属性名大小写不敏感。</summary>
    private sealed class FieldReader(Action<string> warn)
    {
        public JsonElement? Object(JsonElement parent, string name)
        {
            if (!TryGet(parent, name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }

            warn($"设置 {name} 应为对象，已忽略并使用默认值");
            return null;
        }

        public int Int(JsonElement? parent, string name, int fallback, string prefix = "")
        {
            if (!TryGet(parent, name, out var value))
            {
                return fallback;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
            {
                return result;
            }

            warn($"设置 {prefix}{name} 应为整数（实际 {Describe(value)}），回落默认值 {fallback}");
            return fallback;
        }

        public bool Bool(JsonElement? parent, string name, bool fallback, string prefix = "")
        {
            if (!TryGet(parent, name, out var value))
            {
                return fallback;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            warn($"设置 {prefix}{name} 应为 true / false（实际 {Describe(value)}），回落默认值 {(fallback ? "true" : "false")}");
            return fallback;
        }

        public string? String(JsonElement? parent, string name, string? fallback, string prefix = "", bool nullable = false)
        {
            if (!TryGet(parent, name, out var value))
            {
                return fallback;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Null && nullable)
            {
                return null;
            }

            warn($"设置 {prefix}{name} 应为字符串（实际 {Describe(value)}），回落默认值");
            return fallback;
        }

        public string[]? StringArray(JsonElement? parent, string name, string prefix = "")
        {
            if (!TryGet(parent, name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
            {
                return value.EnumerateArray().Select(e => e.GetString()!).ToArray();
            }

            warn($"设置 {prefix}{name} 应为字符串数组，回落默认值 null");
            return null;
        }

        private static bool TryGet(JsonElement? parent, string name, out JsonElement value)
        {
            value = default;
            if (parent is not { ValueKind: JsonValueKind.Object } obj)
            {
                return false;
            }

            if (obj.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            return false;
        }

        private static string Describe(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => $"\"{value.GetString()}\"",
            JsonValueKind.Null => "null",
            _ => value.GetRawText(),
        };
    }
}
