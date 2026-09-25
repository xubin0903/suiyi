namespace Suiyi.Core.Popup;

/// <summary>浮窗显示的内容类别。</summary>
public enum PopupKind
{
    /// <summary>尚未显示过任何内容。</summary>
    None,

    /// <summary>「正在准备翻译服务…」：服务未就绪时触发了翻译。</summary>
    Preparing,

    /// <summary>原文摘要 + 加载指示（指示延迟出现，避免闪烁）。</summary>
    Loading,

    /// <summary>译文。</summary>
    Result,

    /// <summary>错误提示，可重试。</summary>
    Error,
}
