using System.Text.Json;
using System.Text.Json.Serialization;
using Suiyi.Core.Glossary;

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
    /// OCR 模型是否已加载（#53）。旧版引擎没有该字段时为 <see langword="null"/>。
    /// 只表示是否已加载，不表示 OCR 可用；加载失败的原因见 <see cref="OcrError"/>。
    /// </summary>
    [JsonPropertyName("ocr_loaded")]
    public bool? OcrLoaded { get; init; }

    /// <summary>
    /// 最近一次加载 OCR 失败的原因（#53）：形状同 503 <c>ocr_unavailable</c> 的 <c>details</c> 再加 <c>message</c>。
    /// 没有失败、还没尝试加载（未加 <c>--preload-ocr</c> 且没有 OCR 请求）或旧版引擎时为 <see langword="null"/>；加载成功后服务端清空。
    /// </summary>
    [JsonPropertyName("ocr_error")]
    public OcrHealthError? OcrError { get; init; }

    // ---- 术语保护（#83 约定第 3 节）：平铺字段；旧版引擎没有这些字段时均为 null ----

    /// <summary>服务端默认是否开启术语保护（启动参数或环境变量的结果，不反映单次请求的覆盖）。旧版引擎为 <see langword="null"/>。</summary>
    [JsonPropertyName("glossary_enabled")]
    public bool? GlossaryEnabled { get; init; }

    /// <summary>内置术语表条数（按方向展开）。</summary>
    [JsonPropertyName("glossary_builtin_entries")]
    public int? GlossaryBuiltinEntries { get; init; }

    /// <summary>实际使用的用户术语表路径（文件可以不存在）。</summary>
    [JsonPropertyName("glossary_user_path")]
    public string? GlossaryUserPath { get; init; }

    /// <summary>当前生效的用户条目数（按方向展开）；文件不存在或文件级错误时为 0。</summary>
    [JsonPropertyName("glossary_user_entries")]
    public int? GlossaryUserEntries { get; init; }

    /// <summary>文件级错误原因；没有时为 <see langword="null"/>。</summary>
    [JsonPropertyName("glossary_error")]
    public string? GlossaryError { get; init; }

    /// <summary>行级问题（最多 20 条）。</summary>
    [JsonPropertyName("glossary_warnings")]
    public IReadOnlyList<string>? GlossaryWarnings { get; init; }

    /// <summary>
    /// 术语表状态；服务没有报告（旧版引擎，没有 <c>glossary_enabled</c> 与 <c>glossary_builtin_entries</c>）时为 <see langword="null"/>。
    /// </summary>
    public GlossaryStatus? ToGlossaryStatus() => GlossaryFields.ToStatus(
        GlossaryEnabled, GlossaryBuiltinEntries, GlossaryUserEntries, GlossaryUserPath, GlossaryError, GlossaryWarnings);
}

/// <summary><c>POST /glossary/reload</c> 的响应：与 <c>/health</c> 的 <c>glossary_*</c> 字段相同（#83 约定第 2 节）。</summary>
public sealed record GlossaryReloadResponse
{
    /// <summary>同 <see cref="HealthResponse.GlossaryEnabled"/>。</summary>
    [JsonPropertyName("glossary_enabled")]
    public bool? GlossaryEnabled { get; init; }

    /// <summary>同 <see cref="HealthResponse.GlossaryBuiltinEntries"/>。</summary>
    [JsonPropertyName("glossary_builtin_entries")]
    public int? GlossaryBuiltinEntries { get; init; }

    /// <summary>同 <see cref="HealthResponse.GlossaryUserPath"/>。</summary>
    [JsonPropertyName("glossary_user_path")]
    public string? GlossaryUserPath { get; init; }

    /// <summary>同 <see cref="HealthResponse.GlossaryUserEntries"/>。</summary>
    [JsonPropertyName("glossary_user_entries")]
    public int? GlossaryUserEntries { get; init; }

    /// <summary>同 <see cref="HealthResponse.GlossaryError"/>。</summary>
    [JsonPropertyName("glossary_error")]
    public string? GlossaryError { get; init; }

    /// <summary>同 <see cref="HealthResponse.GlossaryWarnings"/>。</summary>
    [JsonPropertyName("glossary_warnings")]
    public IReadOnlyList<string>? GlossaryWarnings { get; init; }

    /// <summary>转为 <see cref="GlossaryStatus"/>；字段缺失时为 <see langword="null"/>。</summary>
    public GlossaryStatus? ToGlossaryStatus() => GlossaryFields.ToStatus(
        GlossaryEnabled, GlossaryBuiltinEntries, GlossaryUserEntries, GlossaryUserPath, GlossaryError, GlossaryWarnings);
}

/// <summary>
/// <c>glossary_*</c> 字段 → <see cref="GlossaryStatus"/>。没有 <c>glossary_enabled</c> 字段即旧版引擎（不支持术语保护），返回 <see langword="null"/>。
/// </summary>
internal static class GlossaryFields
{
    public static GlossaryStatus? ToStatus(
        bool? enabled, int? builtin, int? user, string? path, string? error, IReadOnlyList<string>? warnings)
    {
        if (enabled is null)
        {
            return null;
        }

        return new GlossaryStatus(
            enabled,
            Math.Max(0, builtin ?? 0),
            Math.Max(0, user ?? 0),
            string.IsNullOrWhiteSpace(path) ? null : path,
            string.IsNullOrWhiteSpace(error) ? null : error.Trim(),
            warnings?.Where(w => !string.IsNullOrWhiteSpace(w)).ToArray() ?? []);
    }
}

/// <summary><c>/health.ocr_error</c>：OCR 不可用的原因。</summary>
public sealed record OcrHealthError
{
    /// <summary>
    /// 原因：<c>dependency_missing</c>（没装 <c>engine[ocr]</c>）、<c>models_missing</c>、<c>models_invalid</c>、<c>manifest_unavailable</c>；
    /// 以后可能新增，未知值按模型问题处理。
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>缺失的 OCR 模型 id（可能为空）。</summary>
    [JsonPropertyName("missing_models")]
    public IReadOnlyList<string> MissingModels { get; init; } = [];

    /// <summary>服务端的说明（含服务端路径与命令写法，只写日志，不直接显示）。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
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

    /// <summary>
    /// 本次是否启用专业术语保护（#83 约定：<c>"glossary": true | false</c>，只影响这一次）；
    /// <see langword="null"/> 时不写出，按服务端默认。main 上的旧版服务忽略未知字段（<c>extra="ignore"</c>，有测试覆盖）。
    /// </summary>
    [JsonPropertyName(GlossaryContract.RequestField)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Glossary { get; init; }
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
