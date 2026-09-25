namespace Suiyi.Core.Popup;

/// <summary>
/// 框选翻译（OCR）结果的浮窗内容（#57）。不依赖 HTTP DTO，由 <c>Flow/OcrResultMapper</c> 从引擎响应映射。
/// 段落一一对应：<see cref="TranslationParagraphs"/>[i] 是 <see cref="SourceParagraphs"/>[i] 的译文。
/// </summary>
/// <param name="SourceParagraphs">识别出的原文段落。</param>
/// <param name="TranslationParagraphs">译文段落。</param>
/// <param name="Source">主要原文语种（各段检测结果中最多的一个；未知时 <see langword="null"/>）。</param>
/// <param name="Target">主要目标语种。</param>
public sealed record PopupOcrResult(
    IReadOnlyList<string> SourceParagraphs,
    IReadOnlyList<string> TranslationParagraphs,
    string? Source,
    string Target)
{
    /// <summary>段落之间的分隔（空一行）。</summary>
    public const string ParagraphSeparator = "\n\n";

    /// <summary>空结果（未识别到文字）。</summary>
    public static PopupOcrResult Empty(string target) => new([], [], null, target);

    /// <summary>原文语种是否为自动检测。</summary>
    public bool SourceDetected { get; init; }

    /// <summary>端到端或服务端耗时（可选，显示为小字）。</summary>
    public TimeSpan? Elapsed { get; init; }

    /// <summary>
    /// 译文缺失、用原文代替的段落下标（对应 <see cref="TranslationParagraphs"/>，从 0 起，升序）。
    /// 浮窗据此轻微区分样式（小字提示），复制译文时内容不变。
    /// </summary>
    public IReadOnlyList<int> UntranslatedParagraphs { get; init; } = [];

    /// <summary>是否有译文缺失、用原文代替的段落。</summary>
    public bool HasUntranslated => UntranslatedParagraphs.Count > 0;

    /// <summary>第 <paramref name="index"/> 段是否为用原文代替的段落。</summary>
    /// <param name="index">段落下标（从 0 起）。</param>
    public bool IsUntranslated(int index) => UntranslatedParagraphs.Contains(index);

    /// <summary>是否未识别到任何文字。</summary>
    public bool IsEmpty => SourceParagraphs.All(string.IsNullOrWhiteSpace);

    /// <summary>原文全文（段落间空一行）。</summary>
    public string SourceText => Join(SourceParagraphs);

    /// <summary>译文全文（段落间空一行）。</summary>
    public string TranslationText => Join(TranslationParagraphs);

    private static string Join(IReadOnlyList<string> paragraphs) =>
        string.Join(ParagraphSeparator, paragraphs.Select(p => p.Trim()).Where(p => p.Length > 0));
}
