using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Suiyi.Core.Glossary;

namespace Suiyi.Core.Engine;

/// <summary>
/// 本机翻译服务（<c>python -m suiyi_engine serve</c>）的 HTTP 客户端。契约见 <c>docs/engine/HTTP-API.md</c>。
/// OCR 部分（<c>/ocr</c>、<c>/ocr_translate</c>）按 Issue #53 草案实现，集中在 <c>EngineClient.Ocr.cs</c>。
/// </summary>
/// <remarks>
/// <para>整个进程共用一个实例：内部只有一个长寿命 <see cref="HttpClient"/>，
/// 默认处理器是 <see cref="SocketsHttpHandler"/> 且 <c>UseProxy = false</c>（避免系统代理拦截回环地址），
/// <see cref="HttpClient.Timeout"/> 为无限，每次请求的超时由 <see cref="TimeoutPolicy"/> 与
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> 控制。</para>
/// <para>失败统一抛 <see cref="EngineException"/>；调用方传入的令牌被取消时抛
/// <see cref="OperationCanceledException"/>，不算错误。</para>
/// <para>线程安全。</para>
/// </remarks>
public sealed partial class EngineClient : IDisposable
{
    /// <summary>服务默认端口（与引擎 <c>SUIYI_PORT</c> 默认一致）。</summary>
    public const int DefaultPort = 18780;

    /// <summary>自动检测原文语种时 <c>source</c> 的取值。</summary>
    public const string AutoSource = "auto";

    /// <summary><c>GET /health</c> 的超时（毫秒）。服务承诺翻译进行中也在 200 ms 内返回。</summary>
    public const int HealthTimeoutMs = 2000;

    /// <summary><c>GET /languages</c> 的超时（毫秒）。只扫描目录元数据，不加载模型。</summary>
    public const int LanguagesTimeoutMs = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private LanguagesResponse? _languages;
    private HashSet<string>? _loadedModels;
    private bool? _ocrLoaded;
    private OcrHealthError? _ocrError;
    private GlossaryStatus? _glossary;
    private bool? _glossarySupported;

    /// <summary>连接 <c>http://127.0.0.1:{port}</c>。</summary>
    /// <param name="port">服务端口，默认 <see cref="DefaultPort"/>。</param>
    /// <param name="handler">HTTP 处理器，测试时注入假处理器；默认 <see cref="CreateDefaultHandler"/>。客户端释放时一并释放。</param>
    /// <param name="timeProvider">超时计时用的时钟，测试时注入；默认 <see cref="TimeProvider.System"/>。</param>
    public EngineClient(int port = DefaultPort, HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        BaseAddress = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _http = new HttpClient(handler ?? CreateDefaultHandler(), disposeHandler: true)
        {
            BaseAddress = BaseAddress,
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>服务基址，形如 <c>http://127.0.0.1:18780/</c>。</summary>
    public Uri BaseAddress { get; }

    /// <summary>当前 <c>/languages</c> 缓存；尚未获取或已 <see cref="Invalidate"/> 时为 <see langword="null"/>。</summary>
    public LanguagesResponse? CachedLanguages
    {
        get
        {
            lock (_gate)
            {
                return _languages;
            }
        }
    }

    /// <summary>
    /// 客户端所知的已加载模型：最近一次 <c>/health</c> 的 <c>loaded_models</c>，加上之后成功翻译用过的模型。
    /// 未知时为 <see langword="null"/>。
    /// </summary>
    public IReadOnlyCollection<string>? KnownLoadedModels
    {
        get
        {
            lock (_gate)
            {
                return _loadedModels is null ? null : [.. _loadedModels];
            }
        }
    }

    /// <summary>默认处理器：不走系统代理，不自动解压，连接超时 2 秒。</summary>
    public static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    };

    /// <summary>调用 <c>GET /health</c>，并用其中的 <c>loaded_models</c> 刷新 <see cref="KnownLoadedModels"/>。</summary>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <exception cref="EngineException">服务不可用、超时或响应无法识别。</exception>
    public async Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromMilliseconds(HealthTimeoutMs);
        var health = await SendAsync<HealthResponse>(HttpMethod.Get, "health", null, timeout, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _loadedModels = new HashSet<string>(health.LoadedModels, StringComparer.Ordinal);
            _ocrLoaded = health.OcrLoaded;
            _ocrError = health.OcrError;
            _glossary = health.ToGlossaryStatus();
            _glossarySupported = _glossary is not null;
        }

        return health;
    }

    /// <summary>调用 <c>GET /languages</c>。结果会缓存，直到 <see cref="Invalidate"/>；服务重启或模型变化后由调用方刷新。</summary>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <exception cref="EngineException">服务不可用、超时或响应无法识别。</exception>
    public async Task<LanguagesResponse> GetLanguagesAsync(CancellationToken cancellationToken = default)
    {
        var cached = CachedLanguages;
        if (cached is not null)
        {
            return cached;
        }

        var timeout = TimeSpan.FromMilliseconds(LanguagesTimeoutMs);
        var languages = await SendAsync<LanguagesResponse>(HttpMethod.Get, "languages", null, timeout, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _languages ??= languages;
            return _languages;
        }
    }

    /// <summary>清空 <c>/languages</c> 缓存与已加载模型记录（服务重启、模型目录变化后调用）。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _languages = null;
            _loadedModels = null;
            _ocrLoaded = null;
            _ocrError = null;
            _glossary = null;
            _glossarySupported = null;
        }
    }

    /// <summary>
    /// 计算本次翻译将使用的超时（<see cref="TimeoutPolicy"/>），基于当前缓存，不发请求。
    /// </summary>
    /// <param name="text">原文。</param>
    /// <param name="source">原文语种或 <c>"auto"</c>。</param>
    /// <param name="target">目标语种。</param>
    public TimeSpan GetTimeout(string text, string source, string target)
    {
        LanguagesResponse? languages;
        IReadOnlyCollection<string>? loaded;
        lock (_gate)
        {
            languages = _languages;
            loaded = _loadedModels is null ? null : [.. _loadedModels];
        }

        return TimeoutPolicy.Compute(text, source, target, languages, loaded);
    }

    /// <summary>
    /// 调用 <c>POST /translate</c> 翻译一段文本。超时由 <see cref="TimeoutPolicy"/> 决定。
    /// </summary>
    /// <remarks>
    /// <c>/languages</c> 或已加载模型未知时，先各尝试获取一次（失败则忽略，按「可能懒加载」给 10000 ms）。
    /// 成功后把响应 <c>route</c> 里的模型记为已加载。
    /// </remarks>
    /// <param name="text">原文。</param>
    /// <param name="source">原文语种（ISO 639-1）或 <c>"auto"</c>。</param>
    /// <param name="target">目标语种（ISO 639-1），不能是 <c>"auto"</c>。</param>
    /// <param name="cancellationToken">调用方取消令牌；取消时抛 <see cref="OperationCanceledException"/>。</param>
    /// <exception cref="EngineException">翻译失败，见 <see cref="EngineException.Kind"/>。</exception>
    public async Task<TranslateResponse> TranslateAsync(
        string text,
        string source,
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        await WarmCachesAsync(cancellationToken).ConfigureAwait(false);
        var timeout = GetTimeout(text, source, target);
        var request = new TranslateRequest { Text = text, Source = source, Target = target, Glossary = GlossaryOverride?.Invoke() };
        var response = await SendAsync<TranslateResponse>(HttpMethod.Post, "translate", JsonContent(request), timeout, cancellationToken)
            .ConfigureAwait(false);
        if (response.Route.Count > 0)
        {
            lock (_gate)
            {
                _loadedModels?.UnionWith(response.Route);
            }
        }

        return response;
    }

    /// <summary>翻译，<c>source</c> 为 <c>"auto"</c>。</summary>
    /// <param name="text">原文。</param>
    /// <param name="target">目标语种。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    public Task<TranslateResponse> TranslateAsync(string text, string target, CancellationToken cancellationToken = default) =>
        TranslateAsync(text, AutoSource, target, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task WarmCachesAsync(CancellationToken cancellationToken)
    {
        bool needLanguages;
        bool needHealth;
        lock (_gate)
        {
            needLanguages = _languages is null;
            needHealth = _loadedModels is null;
        }

        try
        {
            if (needLanguages)
            {
                await GetLanguagesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (needHealth)
            {
                await GetHealthAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (EngineException ex) when (ex.Kind != EngineErrorKind.Unavailable)
        {
            // 缓存只影响超时的估计。拿不到时按未知处理，让翻译请求自己报告真正的错误。
        }
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, body.GetType(), JsonOptions), new UTF8Encoding(false), "application/json");

    /// <param name="method">HTTP 方法。</param>
    /// <param name="pathAndQuery">相对路径（可带查询串）。错误信息里只写不带查询串的路径。</param>
    /// <param name="content">请求体，随请求释放；没有时为 <see langword="null"/>。</param>
    /// <param name="timeout">本次请求的超时。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var query = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? pathAndQuery : pathAndQuery[..query];
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var request = new HttpRequestMessage(method, pathAndQuery);
        request.Content = content;

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return Deserialize<T>(payload, path);
            }

            throw MapError((int)response.StatusCode, payload, path);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // 统一带上调用方的令牌，便于调用方用 ex.CancellationToken 判断是谁取消的。
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new EngineException(
                EngineErrorKind.Timeout,
                string.Create(CultureInfo.InvariantCulture, $"{path} 超过 {(int)timeout.TotalMilliseconds} ms 未返回"),
                ex)
            {
                Timeout = timeout,
            };
        }
        catch (HttpRequestException ex)
        {
            throw new EngineException(EngineErrorKind.Unavailable, $"{path} 无法连接服务：{Describe(ex)}", ex);
        }
        catch (IOException ex)
        {
            // 响应读到一半连接被重置。
            throw new EngineException(EngineErrorKind.Unavailable, $"{path} 连接中断：{ex.Message}", ex);
        }
    }

    private static T Deserialize<T>(string payload, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                ?? throw new EngineException(EngineErrorKind.Unknown, $"{path} 返回了 null")
                {
                    StatusCode = 200,
                };
        }
        catch (JsonException ex)
        {
            throw new EngineException(EngineErrorKind.Unknown, $"{path} 返回的 JSON 无法解析：{ex.Message}", ex)
            {
                StatusCode = 200,
            };
        }
    }

    internal static EngineException MapError(int status, string payload, string path)
    {
        var body = TryParseEnvelope(payload);
        if (body is null)
        {
            var kind = status >= 500 ? EngineErrorKind.Internal : EngineErrorKind.Unknown;
            return new EngineException(
                kind,
                string.Create(CultureInfo.InvariantCulture, $"{path} 返回 HTTP {status}，正文不是错误信封"))
            {
                StatusCode = status,
            };
        }

        var details = body.Details;
        var message = string.Create(CultureInfo.InvariantCulture, $"{path} 返回 HTTP {status} {body.Code}：{body.Message}");
        if (MapOcrError(status, body, message) is { } ocrError)
        {
            return ocrError;
        }

        return body.Code switch
        {
            "unsupported_pair" => new EngineException(EngineErrorKind.UnsupportedPair, message)
            {
                StatusCode = status,
                ErrorCode = body.Code,
                Details = details,
                MissingModels = GetStringArray(details, "missing_models"),
                SourceLanguage = GetString(details, "source"),
                TargetLanguage = GetString(details, "target"),
            },
            "text_too_long" => new EngineException(EngineErrorKind.TextTooLong, message)
            {
                StatusCode = status,
                ErrorCode = body.Code,
                Details = details,
                Limit = GetInt(details, "limit"),
                Length = GetInt(details, "length"),
            },
            "detect_failed" => Create(EngineErrorKind.DetectFailed),
            "invalid_request" => Create(EngineErrorKind.InvalidRequest),
            "internal_error" => Create(EngineErrorKind.Internal),
            _ => Create(status >= 500 ? EngineErrorKind.Internal : EngineErrorKind.Unknown),
        };

        EngineException Create(EngineErrorKind kind) => new(kind, message)
        {
            StatusCode = status,
            ErrorCode = body.Code,
            Details = details,
        };
    }

    private static ErrorBody? TryParseEnvelope(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<ErrorEnvelope>(payload, JsonOptions);
            return envelope?.Error is { Code.Length: > 0 } error ? error : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> GetStringArray(JsonElement details, string name)
    {
        if (details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
    }

    private static string? GetString(JsonElement details, string name) =>
        details.ValueKind == JsonValueKind.Object
        && details.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement details, string name) =>
        details.ValueKind == JsonValueKind.Object
        && details.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string Describe(HttpRequestException ex)
    {
        var socket = ex.InnerException as SocketException;
        return socket is null
            ? $"{ex.HttpRequestError}：{ex.Message}"
            : $"{ex.HttpRequestError}/{socket.SocketErrorCode}：{ex.Message}";
    }
}
