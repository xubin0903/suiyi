using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Suiyi.Core.Tests.Engine;

/// <summary>一次被拦截的请求。</summary>
internal sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, string? ContentType, string? CharSet)
{
    /// <summary>查询串（不含 <c>?</c>）。</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>请求体原始字节。</summary>
    public byte[]? BodyBytes { get; init; }
}

/// <summary>按路径应答的假 HTTP 处理器，不监听任何端口。</summary>
internal sealed class FakeEngineHandler : HttpMessageHandler
{
    public const string DefaultLanguages = """
        {"languages":["en","ja","zh"],"pairs":[
          {"src":"en","tgt":"ja","route":"direct","models":["opus-mt-en-jap"]},
          {"src":"en","tgt":"zh","route":"direct","models":["opus-mt-en-zh"]},
          {"src":"ja","tgt":"en","route":"direct","models":["opus-mt-ja-en"]},
          {"src":"ja","tgt":"zh","route":"pivot","models":["opus-mt-ja-en","opus-mt-en-zh"]},
          {"src":"zh","tgt":"en","route":"direct","models":["opus-mt-zh-en"]},
          {"src":"zh","tgt":"ja","route":"direct","models":["opus-mt-tc-big-zh-ja"]}
        ]}
        """;

    public const string AllLoadedHealth = """
        {"status":"ok","version":"0.0.1","models_dir":"/m","uptime_s":1.0,
         "loaded_models":["opus-mt-en-jap","opus-mt-en-zh","opus-mt-ja-en","opus-mt-tc-big-zh-ja","opus-mt-zh-en"]}
        """;

    public const string NothingLoadedHealth = """
        {"status":"ok","version":"0.0.1","models_dir":"/m","uptime_s":1.0,"loaded_models":[]}
        """;

    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public string Languages { get; set; } = DefaultLanguages;

    public string Health { get; set; } = AllLoadedHealth;

    /// <summary>处理 <c>/translate</c>；参数为请求体与（链接了超时的）取消令牌。</summary>
    public Func<string, CancellationToken, Task<HttpResponseMessage>> Translate { get; set; } =
        (_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"text":"","source":"en","detected":true,"target":"zh","route":[],"elapsed_ms":0}"""));

    /// <summary>处理 <c>/ocr_translate</c>；参数为请求记录与取消令牌。默认返回空结果。</summary>
    public Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> OcrTranslate { get; set; } =
        (_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"lines":[],"paragraphs":[],"text":"","image":{"width":10,"height":10},"translation":{"results":[]},"elapsed_ms":{"ocr":1,"translate":0,"total":1}}"""));

    /// <summary>处理 <c>/ocr</c>。</summary>
    public Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> Ocr { get; set; } =
        (_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"lines":[],"paragraphs":[],"text":"","image":{"width":10,"height":10},"elapsed_ms":1}"""));

    /// <summary>处理 <c>POST /glossary/reload</c>；默认 404（旧版引擎没有这个接口）。</summary>
    public Func<RecordedRequest, HttpResponseMessage> GlossaryReload { get; set; } =
        _ => Json(HttpStatusCode.NotFound, """{"detail":"Not Found"}""");

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    public IReadOnlyList<RecordedRequest> OcrRequests => [.. _requests.Where(r => r.Path is "/ocr_translate" or "/ocr")];

    public IReadOnlyList<RecordedRequest> TranslateRequests => [.. _requests.Where(r => r.Path == "/translate")];

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Json(int status, string json) => Json((HttpStatusCode)status, json);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var body = bytes is null ? null : Encoding.UTF8.GetString(bytes);
        var path = request.RequestUri!.AbsolutePath;
        var recorded = new RecordedRequest(
            request.Method,
            path,
            body,
            request.Content?.Headers.ContentType?.MediaType,
            request.Content?.Headers.ContentType?.CharSet)
        {
            Query = request.RequestUri.Query.TrimStart('?'),
            BodyBytes = bytes,
        };
        _requests.Enqueue(recorded);
        return path switch
        {
            "/ocr_translate" => await OcrTranslate(recorded, cancellationToken),
            "/ocr" => await Ocr(recorded, cancellationToken),
            "/health" => Json(HttpStatusCode.OK, Health),
            "/languages" => Json(HttpStatusCode.OK, Languages),
            "/translate" => await Translate(body ?? string.Empty, cancellationToken),
            "/glossary/reload" => GlossaryReload(recorded),
            _ => Json(HttpStatusCode.NotFound, """{"detail":"Not Found"}"""),
        };
    }
}

/// <summary>在 <c>/translate</c> 里挂起、直到令牌取消的应答，并通知测试请求已到达。</summary>
internal sealed class HangingTranslate
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public Task<HttpResponseMessage> HandleRecorded(RecordedRequest _, CancellationToken cancellationToken) =>
        Handle(string.Empty, cancellationToken);

    public async Task<HttpResponseMessage> Handle(string _, CancellationToken cancellationToken)
    {
        _started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>
/// 每次请求都挂起直到令牌取消（超时或调用方取消），并逐次通知测试「第 n 次请求已到达」（#94 超时重试）。
/// 可用 <see cref="Respond"/> 让第 n 次请求（从 1 开始）直接返回。
/// </summary>
internal sealed class HangingRequests
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource> _arrivals = [];
    private int _count;

    /// <summary>第 n 次请求（从 1 开始）不挂起时的应答；为 <see langword="null"/> 或返回 <see langword="null"/> 时挂起。</summary>
    public Func<int, HttpResponseMessage?>? Respond { get; set; }

    /// <summary>已到达的请求数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>等第 <paramref name="n"/> 次请求（从 1 开始）到达，最多等 5 秒。</summary>
    public Task Arrived(int n) => Slot(n).Task.WaitAsync(TimeSpan.FromSeconds(5));

    public Task<HttpResponseMessage> HandleRecorded(RecordedRequest _, CancellationToken cancellationToken) =>
        Handle(string.Empty, cancellationToken);

    public async Task<HttpResponseMessage> Handle(string _, CancellationToken cancellationToken)
    {
        int n;
        lock (_gate)
        {
            n = ++_count;
        }

        var response = Respond?.Invoke(n);
        Slot(n).TrySetResult();
        if (response is not null)
        {
            return response;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }

    private TaskCompletionSource Slot(int n)
    {
        lock (_gate)
        {
            while (_arrivals.Count < n)
            {
                _arrivals.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            return _arrivals[n - 1];
        }
    }
}
