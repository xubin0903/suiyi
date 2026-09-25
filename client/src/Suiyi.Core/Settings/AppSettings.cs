using Suiyi.Core.Clipboard;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Settings;

/// <summary>
/// 客户端设置（<c>settings.json</c>，camelCase）。不可变，用 <c>with</c> 修改后交给 <see cref="SettingsStore.Update"/>。
/// </summary>
public sealed record AppSettings
{
    /// <summary>当前设置文件版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>默认设置。</summary>
    public static AppSettings Default { get; } = new();

    /// <summary>设置文件版本，以后迁移用。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>目标语言：zh / en / ja。</summary>
    public string PrimaryTarget { get; init; } = "zh";

    /// <summary>检测到的原文语种等于 <see cref="PrimaryTarget"/> 时改译为它。</summary>
    public string SecondaryTarget { get; init; } = "en";

    /// <summary>剪贴板监听。</summary>
    public ClipboardSettings Clipboard { get; init; } = new();

    /// <summary>快捷键。</summary>
    public HotkeySettings Hotkey { get; init; } = new();

    /// <summary>译文浮窗。</summary>
    public PopupSettings Popup { get; init; } = new();

    /// <summary>翻译服务启动参数（进程管理 #32 使用）。</summary>
    public EngineSettings Engine { get; init; } = new();

    /// <summary>开机自启（预留，M2 不实现）。</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>改目标语言，并按 <see cref="SettingsRules.ResolveTargets"/> 保持 secondary 与之不同。</summary>
    public AppSettings WithPrimaryTarget(string primary)
    {
        var (p, s) = SettingsRules.ResolveTargets(primary, SecondaryTarget);
        return this with { PrimaryTarget = p, SecondaryTarget = s };
    }
}

/// <summary><c>clipboard.*</c>。</summary>
public sealed record ClipboardSettings
{
    /// <summary>是否监听剪贴板（托盘「暂停监听」切换）。</summary>
    public bool MonitorEnabled { get; init; } = true;

    /// <summary>去抖毫秒数，50–1000。</summary>
    public int DebounceMs { get; init; } = 150;

    /// <summary>自动翻译的最少字符数。</summary>
    public int MinChars { get; init; } = 2;

    /// <summary>自动翻译的最多字符数，上限 10000。</summary>
    public int MaxChars { get; init; } = 2000;

    /// <summary>映射为 <see cref="ClipboardMonitorOptions"/>。</summary>
    public ClipboardMonitorOptions ToMonitorOptions() => ClipboardMonitorOptions.Default with
    {
        Debounce = TimeSpan.FromMilliseconds(DebounceMs),
        Filter = ClipboardFilterOptions.Default with { MinChars = MinChars, MaxChars = MaxChars },
    };
}

/// <summary><c>hotkey.*</c>。</summary>
public sealed record HotkeySettings
{
    /// <summary>翻译快捷键字符串，<c>""</c> 表示禁用。格式由 <c>HotkeyParser</c> 解析，这里不校验。</summary>
    public string Translate { get; init; } = "Ctrl+Alt+T";
}

/// <summary><c>popup.*</c>。</summary>
public sealed record PopupSettings
{
    /// <summary>自动消失秒数，0 表示不自动消失，0–60。</summary>
    public int AutoHideSeconds { get; init; } = 8;

    /// <summary>最大宽度（设备无关像素）。</summary>
    public int MaxWidth { get; init; } = 480;

    /// <summary>映射为 <see cref="PopupOptions"/>。</summary>
    public PopupOptions ToPopupOptions() => new() { AutoHideSeconds = AutoHideSeconds, MaxWidth = MaxWidth };
}

/// <summary><c>engine.*</c>。</summary>
public sealed record EngineSettings
{
    /// <summary>服务端口，与服务默认一致。</summary>
    public int Port { get; init; } = 18780;

    /// <summary>Python 解释器路径；不填则按进程管理的查找顺序。</summary>
    public string? PythonPath { get; init; }

    /// <summary>高级：直接指定服务可执行文件（为 M4 打包 exe 预留）。</summary>
    public string? Command { get; init; }

    /// <summary>高级：<see cref="Command"/> 的参数。</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>模型目录；不填则不传 <c>--models-dir</c>。</summary>
    public string? ModelsDir { get; init; }

    /// <summary>启动时预加载的语向，逗号分隔；<c>""</c> 表示不预加载。</summary>
    public string Preload { get; init; } = "zh-en,en-zh";

    /// <inheritdoc />
    public bool Equals(EngineSettings? other) =>
        other is not null
        && Port == other.Port
        && PythonPath == other.PythonPath
        && Command == other.Command
        && (ReferenceEquals(Args, other.Args) || (Args is not null && other.Args is not null && Args.SequenceEqual(other.Args)))
        && ModelsDir == other.ModelsDir
        && Preload == other.Preload;

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Port);
        hash.Add(PythonPath);
        hash.Add(Command);
        foreach (var arg in Args ?? [])
        {
            hash.Add(arg);
        }

        hash.Add(ModelsDir);
        hash.Add(Preload);
        return hash.ToHashCode();
    }
}
