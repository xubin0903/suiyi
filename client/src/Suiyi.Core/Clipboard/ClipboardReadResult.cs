namespace Suiyi.Core.Clipboard;

/// <summary>一次读取剪贴板的结果状态。</summary>
public enum ClipboardReadStatus
{
    /// <summary>读到了文本（可能为空字符串）。</summary>
    Text,

    /// <summary>剪贴板没有文本格式。</summary>
    NoText,

    /// <summary>剪贴板带隐私标记，未读取正文。</summary>
    PrivateContent,

    /// <summary>剪贴板被占用，本次无法打开。</summary>
    Busy,
}

/// <summary>一次读取剪贴板的结果。</summary>
/// <param name="Status">状态。</param>
/// <param name="Text">仅 <see cref="ClipboardReadStatus.Text"/> 时非空。</param>
public readonly record struct ClipboardReadResult(ClipboardReadStatus Status, string? Text)
{
    /// <summary>读到文本。</summary>
    public static ClipboardReadResult FromText(string text) => new(ClipboardReadStatus.Text, text);

    /// <summary>没有文本格式。</summary>
    public static ClipboardReadResult NoText { get; } = new(ClipboardReadStatus.NoText, null);

    /// <summary>隐私内容，未读取。</summary>
    public static ClipboardReadResult PrivateContent { get; } = new(ClipboardReadStatus.PrivateContent, null);

    /// <summary>剪贴板被占用。</summary>
    public static ClipboardReadResult Busy { get; } = new(ClipboardReadStatus.Busy, null);
}
