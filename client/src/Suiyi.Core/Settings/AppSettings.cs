using Suiyi.Core.Clipboard;
using Suiyi.Core.Engine;
using Suiyi.Core.Glossary;
using Suiyi.Core.Hotkeys;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Settings;

/// <summary>
/// 客户端设置（<c>settings.json</c>，camelCase）。不可变，用 <c>with</c> 修改后交给 <see cref="SettingsStore.Update"/>。
/// </summary>
public sealed record AppSettings
{
    /// <summary>当前设置文件版本。2：新增 <c>hotkey.region</c>（#55）。</summary>
    public const int CurrentSchemaVersion = 2;

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

    /// <summary>专业术语保护（#84）。</summary>
    public GlossarySettings Glossary { get; init; } = new();

    /// <summary>开机自启（预留，M2 不实现）。</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>
    /// 翻译服务配置：<see cref="EngineSettings.ToEngineOptions"/>，加上术语保护开关与用户术语表路径（#84，经环境变量交给服务）。
    /// 环境变量覆盖（<c>EngineOptionsOverrides</c>）在其后应用。
    /// </summary>
    /// <param name="settingsDirectory">设置目录，用户术语表放在这里。</param>
    public EngineOptions ToEngineOptions(string settingsDirectory) => Engine.ToEngineOptions() with
    {
        Glossary = Glossary.Enabled,
        UserGlossaryPath = UserGlossaryFile.ResolvePath(settingsDirectory),
    };

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
    public string Translate { get; init; } = HotkeyParser.DefaultTranslate;

    /// <summary>
    /// 框选翻译快捷键（#55），<c>""</c> 表示禁用。与 <see cref="Translate"/> 不同，读取时即校验：
    /// 格式非法回落默认值；与翻译快捷键相同时禁用（见 <see cref="SettingsRules.Validate"/>）。
    /// </summary>
    public string Region { get; init; } = HotkeyParser.DefaultRegion;
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

/// <summary><c>glossary.*</c>（#84）：与引擎配置同名（#83）。</summary>
public sealed record GlossarySettings
{
    /// <summary>
    /// 专业术语保护开关（托盘「专业术语 ▸ 专业术语保护」切换），默认开启，与引擎默认一致。
    /// 每次翻译随请求带给服务（见 <see cref="GlossaryContract.RequestField"/>），切换后下一次翻译即生效。
    /// </summary>
    public bool Enabled { get; init; } = GlossaryContract.DefaultEnabled;
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
    public string Preload { get; init; } = EngineOptions.DefaultPreload;

    /// <summary>
    /// 启动时预热 OCR 模型（#58，对应服务参数 <c>--preload-ocr</c>，#53）；关闭可省内存，代价是首次框选多等一次冷加载。
    /// 默认开启：缺 OCR 模型或依赖时服务只告警、照常启动（<c>/health.ocr_error</c> 带原因），不影响文本翻译。
    /// </summary>
    public bool PreloadOcr { get; init; } = true;

    /// <summary>映射为进程管理（#32）的 <see cref="EngineOptions"/>；环境变量覆盖由 <c>EngineOptionsOverrides</c> 在其后应用。</summary>
    public EngineOptions ToEngineOptions() => new()
    {
        Port = Port,
        PythonPath = PythonPath,
        Command = Command,
        Args = Args ?? [],
        ModelsDir = ModelsDir,
        Preload = Preload,
        PreloadOcr = PreloadOcr,
    };

    /// <inheritdoc />
    public bool Equals(EngineSettings? other) =>
        other is not null
        && Port == other.Port
        && PythonPath == other.PythonPath
        && Command == other.Command
        && (ReferenceEquals(Args, other.Args) || (Args is not null && other.Args is not null && Args.SequenceEqual(other.Args)))
        && ModelsDir == other.ModelsDir
        && Preload == other.Preload
        && PreloadOcr == other.PreloadOcr;

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
        hash.Add(PreloadOcr);
        return hash.ToHashCode();
    }
}
