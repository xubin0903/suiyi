namespace Suiyi.Core.Clipboard;

/// <summary>捕获到值得翻译的文本。剪贴板监听与快捷键（#33）共用。</summary>
public sealed class ClipboardTextCapturedEventArgs(string text, ClipboardTrigger trigger) : EventArgs
{
    /// <summary>规范化后的文本。</summary>
    public string Text { get; } = text;

    /// <summary>来源。</summary>
    public ClipboardTrigger Trigger { get; } = trigger;
}

/// <summary>内容未被接受。只含原因与长度，不含正文。</summary>
public sealed class ClipboardTextRejectedEventArgs(RejectReason reason, int length, ClipboardTrigger trigger) : EventArgs
{
    /// <summary>原因。</summary>
    public RejectReason Reason { get; } = reason;

    /// <summary>规范化后文本的码位数；未读取正文时为 0。</summary>
    public int Length { get; } = length;

    /// <summary>来源。</summary>
    public ClipboardTrigger Trigger { get; } = trigger;
}
