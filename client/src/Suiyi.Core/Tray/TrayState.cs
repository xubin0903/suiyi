namespace Suiyi.Core.Tray;

/// <summary>
/// 托盘显示状态（不可变）。由服务状态、是否暂停监听与目标语言推导出图标、悬停提示和状态行文字。
/// </summary>
public sealed record TrayState
{
    /// <summary>悬停提示的最大长度（<c>NOTIFYICONDATA.szTip</c> 为 128 个字符，含结尾 NUL）。</summary>
    public const int MaxToolTipLength = 127;

    /// <summary>初始状态：正在准备、未暂停、目标中文。</summary>
    public static TrayState Initial { get; } = new();

    /// <summary>服务状态（由 <see cref="ITrayService.SetStatus"/> 设置）。</summary>
    public TrayStatus Status { get; init; } = TrayStatus.Preparing;

    /// <summary>状态附加说明，例如异常原因。</summary>
    public string? Detail { get; init; }

    /// <summary>是否暂停剪贴板监听。</summary>
    public bool Paused { get; init; }

    /// <summary>目标语言代码。</summary>
    public string Target { get; init; } = "zh";

    /// <summary>
    /// 实际显示的状态：异常与准备中优先；否则暂停监听显示为 <see cref="TrayStatus.Paused"/>，再否则就绪。
    /// </summary>
    public TrayStatus Effective => Status switch
    {
        TrayStatus.Error => TrayStatus.Error,
        TrayStatus.Preparing => TrayStatus.Preparing,
        TrayStatus.Paused => TrayStatus.Paused,
        _ => Paused ? TrayStatus.Paused : TrayStatus.Ready,
    };

    /// <summary>状态行文字（菜单第一行，不含「随译 · 」前缀）。</summary>
    public string StatusText => Effective switch
    {
        TrayStatus.Preparing => "正在准备…",
        TrayStatus.Paused => "已暂停监听",
        TrayStatus.Error => string.IsNullOrWhiteSpace(Detail) ? "翻译服务异常" : $"翻译服务异常：{Detail.Trim()}",
        _ => $"就绪（{TrayLanguages.GetDisplayName(Target)}）",
    };

    /// <summary>悬停提示，例如「随译 · 就绪（中文）」；超长时截断并以「…」结尾。</summary>
    public string ToolTip => Truncate("随译 · " + StatusText.ReplaceLineEndings(" "), MaxToolTipLength);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
}
