using Suiyi.Core.Tray;

namespace Suiyi.Core.Settings;

/// <summary>设置取值规则（纯函数）。</summary>
public static class SettingsRules
{
    /// <summary><c>clipboard.debounceMs</c> 下限。</summary>
    public const int MinDebounceMs = 50;

    /// <summary><c>clipboard.debounceMs</c> 上限。</summary>
    public const int MaxDebounceMs = 1000;

    /// <summary><c>clipboard.maxChars</c> 上限（服务端默认上限）。</summary>
    public const int MaxCharsLimit = 10000;

    /// <summary><c>popup.autoHideSeconds</c> 上限。</summary>
    public const int MaxAutoHideSeconds = 60;

    /// <summary><c>popup.maxWidth</c> 下限。</summary>
    public const int MinPopupWidth = 240;

    /// <summary><c>popup.maxWidth</c> 上限。</summary>
    public const int MaxPopupWidth = 1920;

    /// <summary>
    /// 目标语言与备选目标一致化：二者相同时 secondary 取 <c>primary == "zh" ? "en" : "zh"</c>。
    /// 结果为小写代码。
    /// </summary>
    public static (string Primary, string Secondary) ResolveTargets(string primary, string secondary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primary);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondary);
        var p = primary.Trim().ToLowerInvariant();
        var s = secondary.Trim().ToLowerInvariant();
        if (p == s)
        {
            s = p == "zh" ? "en" : "zh";
        }

        return (p, s);
    }

    /// <summary>
    /// 校验并修正：越界 / 非法字段单独回落默认值（通过 <paramref name="warn"/> 报告），其余字段保留；最后一致化目标语言。
    /// </summary>
    public static AppSettings Validate(AppSettings settings, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        warn ??= _ => { };
        var d = AppSettings.Default;

        var primary = Language(settings.PrimaryTarget, d.PrimaryTarget, "primaryTarget", warn);
        var secondary = Language(settings.SecondaryTarget, d.SecondaryTarget, "secondaryTarget", warn);
        (primary, secondary) = ResolveTargets(primary, secondary);

        var clipboard = settings.Clipboard ?? Fallback(d.Clipboard, "clipboard", warn);
        var debounce = Range(clipboard.DebounceMs, MinDebounceMs, MaxDebounceMs, d.Clipboard.DebounceMs, "clipboard.debounceMs", warn);
        var minChars = Range(clipboard.MinChars, 1, MaxCharsLimit, d.Clipboard.MinChars, "clipboard.minChars", warn);
        var maxChars = Range(clipboard.MaxChars, 1, MaxCharsLimit, d.Clipboard.MaxChars, "clipboard.maxChars", warn);
        if (minChars > maxChars)
        {
            warn($"设置 clipboard.minChars（{minChars}）大于 clipboard.maxChars（{maxChars}），两者回落默认值");
            minChars = d.Clipboard.MinChars;
            maxChars = d.Clipboard.MaxChars;
        }

        var hotkey = settings.Hotkey ?? Fallback(d.Hotkey, "hotkey", warn);
        var translate = hotkey.Translate ?? Fallback(d.Hotkey.Translate, "hotkey.translate", warn);

        var popup = settings.Popup ?? Fallback(d.Popup, "popup", warn);
        var autoHide = Range(popup.AutoHideSeconds, 0, MaxAutoHideSeconds, d.Popup.AutoHideSeconds, "popup.autoHideSeconds", warn);
        var maxWidth = Range(popup.MaxWidth, MinPopupWidth, MaxPopupWidth, d.Popup.MaxWidth, "popup.maxWidth", warn);

        var engine = settings.Engine ?? Fallback(d.Engine, "engine", warn);
        var port = Range(engine.Port, 1, 65535, d.Engine.Port, "engine.port", warn);
        var preload = engine.Preload ?? Fallback(d.Engine.Preload, "engine.preload", warn);
        var args = engine.Args;
        if (args is not null && args.Any(a => a is null))
        {
            warn("设置 engine.args 含 null，已回落默认值");
            args = d.Engine.Args;
        }

        return settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            PrimaryTarget = primary,
            SecondaryTarget = secondary,
            Clipboard = clipboard with { DebounceMs = debounce, MinChars = minChars, MaxChars = maxChars },
            Hotkey = hotkey with { Translate = translate },
            Popup = popup with { AutoHideSeconds = autoHide, MaxWidth = maxWidth },
            Engine = engine with
            {
                Port = port,
                Preload = preload,
                Args = args,
                PythonPath = Blank(engine.PythonPath),
                Command = Blank(engine.Command),
                ModelsDir = Blank(engine.ModelsDir),
            },
        };
    }

    private static string Language(string? value, string fallback, string name, Action<string> warn)
    {
        if (TrayLanguages.IsSupported(value?.Trim()))
        {
            return value!.Trim().ToLowerInvariant();
        }

        warn($"设置 {name} 取值「{value}」不受支持（应为 zh / en / ja），回落默认值 {fallback}");
        return fallback;
    }

    private static int Range(int value, int min, int max, int fallback, string name, Action<string> warn)
    {
        if (value >= min && value <= max)
        {
            return value;
        }

        warn($"设置 {name} 取值 {value} 超出范围 {min}–{max}，回落默认值 {fallback}");
        return fallback;
    }

    private static T Fallback<T>(T fallback, string name, Action<string> warn)
    {
        warn($"设置 {name} 缺失或为 null，回落默认值");
        return fallback;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
