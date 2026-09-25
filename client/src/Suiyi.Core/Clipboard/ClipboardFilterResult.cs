namespace Suiyi.Core.Clipboard;

/// <summary>过滤结果：接受时带规范化后的文本，拒绝时带原因。</summary>
public readonly record struct ClipboardFilterResult
{
    private ClipboardFilterResult(string? text, RejectReason? reason, int length)
    {
        Text = text;
        Reason = reason;
        Length = length;
    }

    /// <summary>是否接受。</summary>
    public bool IsAccepted => Reason is null;

    /// <summary>接受时为规范化后的文本（去首尾空白、换行统一为 <c>\n</c>），拒绝时为 <see langword="null"/>。</summary>
    public string? Text { get; }

    /// <summary>拒绝原因；接受时为 <see langword="null"/>。</summary>
    public RejectReason? Reason { get; }

    /// <summary>规范化后文本的码位数，用于日志。</summary>
    public int Length { get; }

    /// <summary>接受。</summary>
    public static ClipboardFilterResult Accept(string text, int length) => new(text, null, length);

    /// <summary>拒绝。</summary>
    public static ClipboardFilterResult Reject(RejectReason reason, int length) => new(null, reason, length);
}
