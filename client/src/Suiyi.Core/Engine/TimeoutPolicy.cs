namespace Suiyi.Core.Engine;

/// <summary>
/// <c>POST /translate</c>（及 OCR 请求，见 <see cref="OcrMs"/>）的客户端超时策略（纯函数）。阈值依据
/// <c>docs/engine/性能基线.md</c>「给 M2 客户端」：短句 1500 ms，段落与英文中转 3000 ms；
/// 首次加载模型另给 10000 ms。
/// </summary>
/// <remarks>
/// 规则按顺序：
/// <list type="number">
/// <item>候选路线：<c>source</c> 明确时为该语向；<c>auto</c> 时为 {zh, en, ja} 去掉 <c>target</c> 后，到 <c>target</c> 的全部可用语向。</item>
/// <item>任一候选路线需要的模型不在 <c>loaded_models</c>（会触发懒加载），或 <c>/languages</c>、<c>loaded_models</c> 未知 → <see cref="LazyLoadMs"/>。</item>
/// <item>否则文本不超过 <see cref="ShortTextMaxChars"/> 字符、不含换行，且候选路线都是直连 → <see cref="ShortDirectMs"/>。</item>
/// <item>否则 → <see cref="ParagraphMs"/>；超过 <see cref="LongTextThresholdChars"/> 字符后每字符加 1 ms，封顶 <see cref="MaxMs"/>。</item>
/// </list>
/// 长度按 Unicode 码位计，与服务端 Python <c>len</c> 一致。懒加载的 10000 ms 不低于第 4 条对同一文本算出的值，
/// 避免「模型未加载时的超时反而比已加载时短」。
/// <para>#94：引擎会空闲卸载模型（<c>/health.model_idle_unload_s</c>）。<see cref="EngineClient"/> 传入的 <c>loadedModels</c>
/// 已按 <see cref="ColdStartRule"/> 去掉可能被卸载的模型，所以冷方向落到规则 2；本类不关心卸载细节。</para>
/// </remarks>
public static class TimeoutPolicy
{
    /// <summary>候选路线需要懒加载模型时的超时（毫秒）。性能基线里 big 模型加载约 0.4–0.5 s，Windows 冷盘留足余量。</summary>
    public const int LazyLoadMs = 10000;

    /// <summary>短句直连超时（毫秒）。见 <c>docs/engine/性能基线.md</c>「给 M2 客户端」：<c>max(1500, P95×3)</c>。</summary>
    public const int ShortDirectMs = 1500;

    /// <summary>段落或英文中转的超时（毫秒）。见 <c>docs/engine/性能基线.md</c>「给 M2 客户端」：<c>max(3000, P95×2)</c>。</summary>
    public const int ParagraphMs = 3000;

    /// <summary>短句的最大字符数（含）。</summary>
    public const int ShortTextMaxChars = 120;

    /// <summary>超过该字符数后按 <see cref="ExtraMsPerChar"/> 加时。</summary>
    public const int LongTextThresholdChars = 2000;

    /// <summary>超过 <see cref="LongTextThresholdChars"/> 后每个字符增加的毫秒数。</summary>
    public const int ExtraMsPerChar = 1;

    /// <summary>超时上限（毫秒）。</summary>
    public const int MaxMs = 15000;

    /// <summary><c>source=auto</c> 时参与候选的原文语种（产品只做中英日自动检测）。</summary>
    public static IReadOnlyList<string> AutoSourceLanguages { get; } = ["zh", "en", "ja"];

    /// <summary>计算一次翻译请求的超时。</summary>
    /// <param name="text">原文。</param>
    /// <param name="source">原文语种或 <c>"auto"</c>。</param>
    /// <param name="target">目标语种。</param>
    /// <param name="languages"><c>/languages</c> 缓存；未知时传 <see langword="null"/>。</param>
    /// <param name="loadedModels"><c>/health.loaded_models</c>（可含之后成功翻译用过的模型）；未知时传 <see langword="null"/>。</param>
    public static TimeSpan Compute(
        string text,
        string source,
        string target,
        LanguagesResponse? languages,
        IReadOnlyCollection<string>? loadedModels) =>
        TimeSpan.FromMilliseconds(ComputeMilliseconds(text, source, target, languages, loadedModels));

    /// <summary>同 <see cref="Compute"/>，以毫秒返回。</summary>
    /// <param name="text">原文。</param>
    /// <param name="source">原文语种或 <c>"auto"</c>。</param>
    /// <param name="target">目标语种。</param>
    /// <param name="languages"><c>/languages</c> 缓存；未知时传 <see langword="null"/>。</param>
    /// <param name="loadedModels">已加载模型；未知时传 <see langword="null"/>。</param>
    public static int ComputeMilliseconds(
        string text,
        string source,
        string target,
        LanguagesResponse? languages,
        IReadOnlyCollection<string>? loadedModels)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var length = CountChars(text);
        var paragraph = ParagraphTimeout(length);

        if (languages is null || loadedModels is null)
        {
            return Math.Max(LazyLoadMs, paragraph);
        }

        var candidates = CandidateRoutes(source, target, languages);
        if (candidates.Any(pair => pair.Models.Any(model => !loadedModels.Contains(model))))
        {
            return Math.Max(LazyLoadMs, paragraph);
        }

        var isShort = length <= ShortTextMaxChars && !ContainsNewline(text);
        if (isShort && candidates.All(pair => pair.IsDirect))
        {
            return ShortDirectMs;
        }

        return paragraph;
    }

    /// <summary>
    /// 冷启动超时（毫秒）：按「模型需要重新加载」处理，即 <c>max(</c><see cref="LazyLoadMs"/><c>, 段落超时)</c>，
    /// 与规则 2（懒加载）对同一文本的值相同。用于 #94 超时后的自动重试。
    /// </summary>
    /// <param name="text">原文。</param>
    public static int ComputeColdMilliseconds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Math.Max(LazyLoadMs, ParagraphTimeout(CountChars(text)));
    }

    /// <summary>规则 1：本次请求可能走的语向（只含 <c>/languages</c> 里存在的）。</summary>
    /// <param name="source">原文语种或 <c>"auto"</c>。</param>
    /// <param name="target">目标语种。</param>
    /// <param name="languages"><c>/languages</c> 缓存。</param>
    public static IReadOnlyList<LanguagePair> CandidateRoutes(string source, string target, LanguagesResponse languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var tgt = Normalize(target);
        var src = Normalize(source);
        IEnumerable<string> sources = src == EngineClient.AutoSource
            ? AutoSourceLanguages.Where(lang => lang != tgt)
            : src == tgt ? [] : [src];
        return sources
            .Select(lang => languages.Find(lang, tgt))
            .OfType<LanguagePair>()
            .ToList();
    }

    /// <summary>按 Unicode 码位计数（代理对算 1 个字符）。</summary>
    /// <param name="text">文本。</param>
    public static int CountChars(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// OCR 请求（<c>/ocr</c>、<c>/ocr_translate</c>）在 OCR 与翻译模型都已加载时的超时（毫秒）。
    /// 取 Issue #56 建议的 15 s：目前没有 OCR 性能基线（等 #52），框选区域一般不超过一屏，
    /// OCR 本身按 CPU 秒级估计，其余留给多段翻译（每段已加载时 ≤ <see cref="ParagraphMs"/>）。有了基线后按 P95 调整。
    /// </summary>
    public const int OcrMs = 15000;

    /// <summary>
    /// OCR 模型或候选翻译模型可能需要冷加载时的超时（毫秒）：<see cref="OcrMs"/> 加上 OCR 模型冷加载的余量，
    /// 再留 <see cref="LazyLoadMs"/> 给翻译模型懒加载。<c>/health</c> 不可知（旧引擎没有 <c>ocr_loaded</c>）时也用此值。
    /// </summary>
    public const int OcrColdMs = 30000;

    /// <summary>计算一次 <c>/ocr_translate</c> 的超时（毫秒）。</summary>
    /// <remarks>
    /// <see cref="OcrMs"/> 的条件：<paramref name="ocrLoaded"/> 为 <see langword="true"/>，<c>/languages</c> 与已加载模型已知，
    /// 且 <c>source→target</c> 的候选路线（见 <see cref="CandidateRoutes"/>）和 <c>target→fallbackTarget</c> 路线需要的模型都已加载；
    /// 否则 <see cref="OcrColdMs"/>。
    /// </remarks>
    /// <param name="source">原文语种或 <c>"auto"</c>。</param>
    /// <param name="target">目标语种。</param>
    /// <param name="fallbackTarget">次目标；没有时为 <see langword="null"/>。</param>
    /// <param name="ocrLoaded"><c>/health.ocr_loaded</c>（或之后成功识别过）；未知时为 <see langword="null"/>。</param>
    /// <param name="languages"><c>/languages</c> 缓存；未知时传 <see langword="null"/>。</param>
    /// <param name="loadedModels">已加载的翻译模型；未知时传 <see langword="null"/>。</param>
    public static int ComputeOcrTranslateMilliseconds(
        string source,
        string target,
        string? fallbackTarget,
        bool? ocrLoaded,
        LanguagesResponse? languages,
        IReadOnlyCollection<string>? loadedModels)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (ocrLoaded != true || languages is null || loadedModels is null)
        {
            return OcrColdMs;
        }

        var candidates = CandidateRoutes(source, target, languages).ToList();
        if (!string.IsNullOrWhiteSpace(fallbackTarget))
        {
            candidates.AddRange(CandidateRoutes(target, fallbackTarget, languages));
        }

        return candidates.Any(pair => pair.Models.Any(model => !loadedModels.Contains(model))) ? OcrColdMs : OcrMs;
    }

    /// <summary>计算一次 <c>/ocr</c>（只识别）的超时（毫秒）：OCR 模型已加载时 <see cref="OcrMs"/>，否则 <see cref="OcrColdMs"/>。</summary>
    /// <param name="ocrLoaded"><c>/health.ocr_loaded</c>；未知时为 <see langword="null"/>。</param>
    public static int ComputeOcrMilliseconds(bool? ocrLoaded) => ocrLoaded == true ? OcrMs : OcrColdMs;

    private static int ParagraphTimeout(int length)
    {
        if (length <= LongTextThresholdChars)
        {
            return ParagraphMs;
        }

        var extra = (long)(length - LongTextThresholdChars) * ExtraMsPerChar;
        return (int)Math.Min(MaxMs, ParagraphMs + extra);
    }

    private static bool ContainsNewline(string text) => text.AsSpan().IndexOfAny('\n', '\r') >= 0;

    internal static string Normalize(string code)
    {
        var trimmed = code.Trim().ToLowerInvariant().Replace('_', '-');
        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? trimmed[..dash] : trimmed;
    }
}
