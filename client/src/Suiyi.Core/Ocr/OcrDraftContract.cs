using System.Text.Json;
using System.Text.Json.Serialization;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Ocr;

// ⚠ 按 Issue #53 的接口草案实现（docs/engine/HTTP-API.md 的 OCR 部分尚未定稿合入）。
// 客户端里依赖草案字段名、错误码的地方只有本文件与 Flow/OcrResultMapper.cs；
// #53 定稿或 #56 接入 EngineClient 时，按定稿同步这两处（及对应单测），其余代码只用 PopupOcrResult / PopupError。
// 与 EngineDtos 一样忽略未知字段，不要打开 UnmappedMemberHandling.Disallow。

/// <summary><c>POST /ocr_translate</c> 的响应（#53 草案）：<c>/ocr</c> 的全部字段 + <c>translation</c> + 分段耗时。</summary>
public sealed record OcrTranslateResponse
{
    /// <summary>识别出的文本行（带框与置信度）。浮窗不直接使用。</summary>
    [JsonPropertyName("lines")]
    public IReadOnlyList<OcrLine> Lines { get; init; } = [];

    /// <summary>合并后的段落，顺序即阅读顺序；识别为空时为空数组。</summary>
    [JsonPropertyName("paragraphs")]
    public IReadOnlyList<OcrParagraph> Paragraphs { get; init; } = [];

    /// <summary>段落以 <c>\n</c> 连接的全文；识别为空时为 <c>""</c>。</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>服务端解码出的图片尺寸（像素）。</summary>
    [JsonPropertyName("image")]
    public OcrImageInfo? Image { get; init; }

    /// <summary>译文：与 <c>/translate</c> 批量结果同构，<c>results</c> 与 <see cref="Paragraphs"/> 按顺序一一对应。</summary>
    [JsonPropertyName("translation")]
    public OcrTranslation? Translation { get; init; }

    /// <summary>分段耗时（毫秒）。</summary>
    [JsonPropertyName("elapsed_ms")]
    public OcrElapsed? ElapsedMs { get; init; }
}

/// <summary>文本行。</summary>
public sealed record OcrLine
{
    /// <summary>行文本。</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>框（草案未定格式，原样保留）。</summary>
    [JsonPropertyName("box")]
    public JsonElement Box { get; init; }

    /// <summary>置信度 0–1。</summary>
    [JsonPropertyName("score")]
    public double Score { get; init; }
}

/// <summary>段落。</summary>
public sealed record OcrParagraph
{
    /// <summary>段落文本（段内换行已合并）。</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>框（草案未定格式，原样保留）。</summary>
    [JsonPropertyName("box")]
    public JsonElement Box { get; init; }
}

/// <summary>图片尺寸。</summary>
public sealed record OcrImageInfo
{
    /// <summary>宽（像素）。</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>高（像素）。</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }
}

/// <summary><c>translation</c>：与 <c>/translate</c> 批量响应同构。</summary>
public sealed record OcrTranslation
{
    /// <summary>每个段落一条，与单条 <c>/translate</c> 响应相同（各自带检测到的 <c>source</c>、实际 <c>target</c>）。</summary>
    [JsonPropertyName("results")]
    public IReadOnlyList<TranslateResponse> Results { get; init; } = [];
}

/// <summary><c>/ocr_translate</c> 的 <c>elapsed_ms</c>。</summary>
public sealed record OcrElapsed
{
    /// <summary>OCR 耗时。</summary>
    [JsonPropertyName("ocr")]
    public double Ocr { get; init; }

    /// <summary>翻译耗时。</summary>
    [JsonPropertyName("translate")]
    public double Translate { get; init; }

    /// <summary>服务端总耗时。</summary>
    [JsonPropertyName("total")]
    public double Total { get; init; }
}

/// <summary>#53 草案新增的错误码（翻译侧沿用 <c>unsupported_pair</c> / <c>text_too_long</c> / <c>detect_failed</c>）。</summary>
public static class OcrErrorCodes
{
    /// <summary>413：请求体超过字节上限或像素超过上限；<c>details</c> 含 <c>limit</c>、<c>actual</c>。</summary>
    public const string ImageTooLarge = "image_too_large";

    /// <summary>415：不是 PNG（按魔数判定）。</summary>
    public const string UnsupportedMediaType = "unsupported_media_type";

    /// <summary>422：PNG 无法解码。</summary>
    public const string InvalidImage = "invalid_image";

    /// <summary>503：OCR 模型缺失；<c>details.missing_models</c>。</summary>
    public const string OcrUnavailable = "ocr_unavailable";

    /// <summary>沿用 <c>/translate</c>：语向没有模型。</summary>
    public const string UnsupportedPair = "unsupported_pair";

    /// <summary>沿用 <c>/translate</c>：段落超过字符上限。</summary>
    public const string TextTooLong = "text_too_long";

    /// <summary>沿用 <c>/translate</c>：检测不出语种。</summary>
    public const string DetectFailed = "detect_failed";
}

/// <summary>草案 JSON 的解析（演示数据与单测用；#56 的 <c>EngineClient</c> 可复用同一组 DTO）。</summary>
public static class OcrDraftContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>解析 <c>/ocr_translate</c> 响应正文。</summary>
    /// <exception cref="JsonException">不是合法 JSON 或根不是对象。</exception>
    public static OcrTranslateResponse ParseResponse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<OcrTranslateResponse>(json, JsonOptions)
            ?? throw new JsonException("响应正文为 null");
    }

    /// <summary>解析错误信封正文（沿用 <c>error.code/message/details</c>）。不是信封时返回 <see langword="null"/>。</summary>
    public static ErrorBody? ParseError(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<ErrorEnvelope>(json, JsonOptions)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
