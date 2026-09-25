using System.Text.Json;
using Suiyi.Core.Engine;
using Suiyi.Core.Ocr;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Flow;

/// <summary>
/// 引擎 OCR 响应 / 错误 → 浮窗内容（纯函数）。⚠ 按 Issue #53 接口草案实现，与 <see cref="OcrDraftContract"/> 一起是客户端里
/// 唯二依赖草案字段的地方；#53 定稿后在这里同步。
/// </summary>
public static class OcrResultMapper
{
    /// <summary>
    /// 映射成功响应。
    /// <list type="bullet">
    /// <item>原文段落取 <c>paragraphs[].text</c>；<c>paragraphs</c> 为空但 <c>text</c> 非空时按 <c>\n</c> 拆段兜底。</item>
    /// <item>译文取 <c>translation.results[i].text</c>，按下标与段落对应；缺失的段落用原文代替（不丢内容），
    /// 并记入 <see cref="PopupOcrResult.UntranslatedParagraphs"/>，浮窗轻微区分样式。</item>
    /// <item>语种标签取各段 <c>source</c> / <c>target</c> 中出现最多的（并列取靠前的）；有一段自动检测即视为自动。</item>
    /// <item>耗时取 <c>elapsed_ms.total</c>（没有时为 <see langword="null"/>）。</item>
    /// <item>空白段落丢弃；全部为空 → <see cref="PopupOcrResult.IsEmpty"/>（「未识别到文字」）。</item>
    /// </list>
    /// </summary>
    /// <param name="response">响应。</param>
    /// <param name="requestedTarget">请求的目标语种（响应里没有译文时用于标签）。</param>
    public static PopupOcrResult Map(OcrTranslateResponse response, string requestedTarget)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedTarget);

        var paragraphs = response.Paragraphs.Count > 0
            ? response.Paragraphs.Select(p => p.Text ?? string.Empty).ToList()
            : (response.Text ?? string.Empty).Split('\n').ToList();
        var results = response.Translation?.Results ?? [];

        var sources = new List<string>();
        var translations = new List<string>();
        var used = new List<TranslateResponse>();
        var untranslated = new List<int>();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var source = paragraphs[i].Trim();
            if (source.Length == 0)
            {
                continue;
            }

            var result = i < results.Count ? results[i] : null;
            var translation = result?.Text?.Trim();
            sources.Add(source);
            if (string.IsNullOrEmpty(translation))
            {
                untranslated.Add(translations.Count);
                translations.Add(source);
            }
            else
            {
                translations.Add(translation);
            }

            if (result is not null)
            {
                used.Add(result);
            }
        }

        var elapsed = response.ElapsedMs is { Total: > 0 } e ? TimeSpan.FromMilliseconds(e.Total) : (TimeSpan?)null;
        if (sources.Count == 0)
        {
            return PopupOcrResult.Empty(requestedTarget) with { Elapsed = elapsed };
        }

        return new PopupOcrResult(
            sources,
            translations,
            MostCommon(used.Select(r => r.Source)),
            MostCommon(used.Select(r => r.Target)) ?? requestedTarget)
        {
            SourceDetected = used.Any(r => r.Detected),
            Elapsed = elapsed,
            UntranslatedParagraphs = untranslated,
        };
    }

    /// <summary>
    /// 映射错误信封（<c>error.code</c> + <c>details</c>）。OCR 新增错误码见 <see cref="OcrErrorCodes"/>；
    /// 翻译侧错误码与 <see cref="PopupErrorMapper"/> 的中文提示一致。未知错误码 → <see cref="PopupErrorKind.Other"/>。
    /// 连接失败、超时等没有信封的错误仍由 <see cref="PopupErrorMapper.Map"/> 处理。
    /// </summary>
    public static PopupError MapError(string? code, JsonElement details = default)
    {
        return code switch
        {
            OcrErrorCodes.ImageTooLarge => new PopupError(PopupErrorKind.ImageTooLarge)
            {
                Limit = Int(details, "limit"),
                Length = Int(details, "actual"),
            },
            OcrErrorCodes.UnsupportedMediaType or OcrErrorCodes.InvalidImage => new PopupError(PopupErrorKind.InvalidImage),
            OcrErrorCodes.OcrUnavailable => new PopupError(PopupErrorKind.OcrUnavailable)
            {
                MissingModels = Strings(details, "missing_models"),
                OcrReason = Text(details, "reason"),
            },
            OcrErrorCodes.UnsupportedPair => new PopupError(PopupErrorKind.MissingModels) { MissingModels = Strings(details, "missing_models") },
            OcrErrorCodes.TextTooLong => new PopupError(PopupErrorKind.TextTooLong) { Limit = Int(details, "limit"), Length = Int(details, "length") },
            OcrErrorCodes.DetectFailed => new PopupError(PopupErrorKind.DetectFailed),
            _ => new PopupError(PopupErrorKind.Other) { Detail = "识别或翻译失败，请重试" },
        };
    }

    /// <summary>映射错误信封对象。</summary>
    public static PopupError MapError(ErrorBody? error) => MapError(error?.Code, error?.Details ?? default);

    private static string? MostCommon(IEnumerable<string?> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim().ToLowerInvariant()).ToList();
        return list.Count == 0
            ? null
            : list.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => list.IndexOf(g.Key)).First().Key;
    }

    private static int? Int(JsonElement details, string name) =>
        details.ValueKind == JsonValueKind.Object && details.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;

    private static string? Text(JsonElement details, string name) =>
        details.ValueKind == JsonValueKind.Object && details.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string[] Strings(JsonElement details, string name) =>
        details.ValueKind == JsonValueKind.Object && details.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];
}
