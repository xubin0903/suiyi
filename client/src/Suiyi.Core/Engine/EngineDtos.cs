using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suiyi.Core.Engine;

// 契约见 docs/engine/HTTP-API.md。0.0.x 内字段只增不改；System.Text.Json 默认忽略未知字段，
// 这里不要打开 UnmappedMemberHandling.Disallow。

/// <summary><c>GET /health</c> 的响应。</summary>
public sealed record HealthResponse
{
    /// <summary>固定为 <c>"ok"</c>。</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>引擎包版本，如 <c>0.0.1</c>。</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>服务进程使用的模型目录。</summary>
    [JsonPropertyName("models_dir")]
    public string ModelsDir { get; init; } = string.Empty;

    /// <summary>已经加载进内存的模型 id（字典序）。</summary>
    [JsonPropertyName("loaded_models")]
    public IReadOnlyList<string> LoadedModels { get; init; } = [];

    /// <summary>自开始监听起的秒数。</summary>
    [JsonPropertyName("uptime_s")]
    public double UptimeSeconds { get; init; }

    /// <summary>
    /// OCR 模型是否已加载（⚠ #53 草案新增字段）。旧版引擎没有该字段时为 <see langword="null"/>。
    /// 只表示是否已预热，不表示 OCR 可用：模型缺失要到 <c>/ocr_translate</c> 返回 <c>ocr_unavailable</c> 才知道。
    /// </summary>
    [JsonPropertyName("ocr_loaded")]
    public bool? OcrLoaded { get; init; }
}

/// <summary><c>GET /languages</c> 的响应：当前模型目录实际能翻译的语向。</summary>
public sealed record LanguagesResponse
{
    /// <summary>语向里出现过的语种代码，按字母排序。</summary>
    [JsonPropertyName("languages")]
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>可用语向。</summary>
    [JsonPropertyName("pairs")]
    public IReadOnlyList<LanguagePair> Pairs { get; init; } = [];

    /// <summary>查找 <paramref name="source"/>→<paramref name="target"/> 语向，没有时返回 <see langword="null"/>。</summary>
    /// <param name="source">原文语种（ISO 639-1）。</param>
    /// <param name="target">目标语种（ISO 639-1）。</param>
    public LanguagePair? Find(string source, string target) =>
        Pairs.FirstOrDefault(p =>
            string.Equals(p.Source, source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase));
}

/// <summary><c>/languages</c> 里的一条语向。</summary>
public sealed record LanguagePair
{
    /// <summary><see cref="Route"/> 为直连时的取值。</summary>
    public const string DirectRoute = "direct";

    /// <summary><see cref="Route"/> 为英文中转时的取值。</summary>
    public const string PivotRoute = "pivot";

    /// <summary>原文语种。</summary>
    [JsonPropertyName("src")]
    public string Source { get; init; } = string.Empty;

    /// <summary>目标语种。</summary>
    [JsonPropertyName("tgt")]
    public string Target { get; init; } = string.Empty;

    /// <summary><c>"direct"</c> 或 <c>"pivot"</c>。注意与 <see cref="TranslateResponse.Route"/>（模型 id 数组）不是同一类型。</summary>
    [JsonPropertyName("route")]
    public string Route { get; init; } = string.Empty;

    /// <summary>该语向需要的模型 id，按调用顺序。</summary>
    [JsonPropertyName("models")]
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>是否直连。</summary>
    [JsonIgnore]
    public bool IsDirect => string.Equals(Route, DirectRoute, StringComparison.Ordinal);
}

/// <summary><c>POST /translate</c> 的单条请求体。</summary>
public sealed record TranslateRequest
{
    /// <summary>原文。</summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>原文语种，默认 <c>"auto"</c>（由服务检测）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = EngineClient.AutoSource;

    /// <summary>目标语种，不能是 <c>"auto"</c>。</summary>
    [JsonPropertyName("target")]
    public required string Target { get; init; }
}

/// <summary><c>POST /translate</c> 的单条响应。</summary>
public sealed record TranslateResponse
{
    /// <summary>译文。检测结果与目标相同时为原文。</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>实际原文语种（<c>auto</c> 时为检测结果）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    /// <summary>原文语种是否由服务检测得到。</summary>
    [JsonPropertyName("detected")]
    public bool Detected { get; init; }

    /// <summary>目标语种。</summary>
    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    /// <summary>实际使用的模型 id。空数组表示原样返回；长度 2 表示英文中转。</summary>
    [JsonPropertyName("route")]
    public IReadOnlyList<string> Route { get; init; } = [];

    /// <summary>服务端翻译耗时（毫秒），含首次加载模型，不含检测与 HTTP。</summary>
    [JsonPropertyName("elapsed_ms")]
    public double ElapsedMs { get; init; }
}

/// <summary>非 200 响应的错误信封 <c>{"error": {...}}</c>。</summary>
public sealed record ErrorEnvelope
{
    /// <summary>错误主体。框架自己的 404 等不是信封，此时为 <see langword="null"/>。</summary>
    [JsonPropertyName("error")]
    public ErrorBody? Error { get; init; }
}

/// <summary>错误信封里的 <c>error</c> 对象。</summary>
public sealed record ErrorBody
{
    /// <summary>错误码，如 <c>unsupported_pair</c>。</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>服务端的中文说明（仅供日志，界面用 <see cref="EngineException.UserMessage"/>）。</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>附加信息，始终是对象，可能多出键。</summary>
    [JsonPropertyName("details")]
    public JsonElement Details { get; init; }
}
