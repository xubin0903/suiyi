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

    /// <summary>框选翻译未识别到文字（#57）：普通提示样式，不是错误。</summary>
    Empty,
}

/// <summary>浮窗内容来源：复制翻译（M2）或框选翻译（M3 OCR）。</summary>
public enum PopupContentMode
{
    /// <summary>复制 / 快捷键取词翻译（M2）。定位到光标旁。</summary>
    Text,

    /// <summary>框选翻译（#57）：有原文区、复制原文，定位到选区旁。</summary>
    Ocr,
}
