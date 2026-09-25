namespace Suiyi.Core.Clipboard;

/// <summary>捕获到值得翻译的文本。剪贴板监听与快捷键（#33）共用。</summary>
/// <param name="text">规范化后的文本。</param>
/// <param name="trigger">来源。</param>
/// <param name="timestamp">
/// 触发时刻（<see cref="TimeProvider.GetTimestamp"/>）：监听为最后一次 <c>WM_CLIPBOARDUPDATE</c>，快捷键为按下时；
/// 0 表示未知。用于端到端延迟度量。
/// </param>
public sealed class ClipboardTextCapturedEventArgs(string text, ClipboardTrigger trigger, long timestamp = 0) : EventArgs
{
    /// <summary>规范化后的文本。</summary>
    public string Text { get; } = text;

    /// <summary>来源。</summary>
    public ClipboardTrigger Trigger { get; } = trigger;

    /// <summary>触发时刻（<see cref="TimeProvider.GetTimestamp"/>），0 表示未知。</summary>
    public long Timestamp { get; } = timestamp;
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
