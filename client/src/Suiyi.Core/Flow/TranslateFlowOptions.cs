using Suiyi.Core.Clipboard;

namespace Suiyi.Core.Flow;

/// <summary>主流程参数。</summary>
public sealed record TranslateFlowOptions
{
    /// <summary>
    /// 服务未就绪时最多等多久（默认 30 s，与服务启动超时一致）：期间浮窗显示「正在准备」，就绪后自动补译最后一次请求；
    /// 超时显示 <see cref="Popup.PopupErrorKind.EngineStartTimeout"/>（可重试）。
    /// </summary>
    public TimeSpan ReadyWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>托盘「翻译剪贴板」的过滤规则：与快捷键一致，最多 10000 字、不做重复抑制。</summary>
    public ClipboardFilterOptions ManualFilter { get; init; } =
        ClipboardFilterOptions.Default with { MinChars = 1, MaxChars = 10000, DuplicateWindow = TimeSpan.Zero };

    /// <summary>延迟统计窗口，默认 20 次。</summary>
    public int LatencyWindow { get; init; } = 20;
}
