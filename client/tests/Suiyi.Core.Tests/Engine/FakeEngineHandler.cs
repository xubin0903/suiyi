using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Suiyi.Core.Tests.Engine;

/// <summary>一次被拦截的请求。</summary>
internal sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, string? ContentType, string? CharSet);

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

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    public IReadOnlyList<RecordedRequest> TranslateRequests => [.. _requests.Where(r => r.Path == "/translate")];

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Json(int status, string json) => Json((HttpStatusCode)status, json);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        _requests.Enqueue(new RecordedRequest(
            request.Method,
            path,
            body,
            request.Content?.Headers.ContentType?.MediaType,
            request.Content?.Headers.ContentType?.CharSet));
        return path switch
        {
            "/health" => Json(HttpStatusCode.OK, Health),
            "/languages" => Json(HttpStatusCode.OK, Languages),
            "/translate" => await Translate(body ?? string.Empty, cancellationToken),
            _ => Json(HttpStatusCode.NotFound, """{"detail":"Not Found"}"""),
        };
    }
}

/// <summary>在 <c>/translate</c> 里挂起、直到令牌取消的应答，并通知测试请求已到达。</summary>
internal sealed class HangingTranslate
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public async Task<HttpResponseMessage> Handle(string _, CancellationToken cancellationToken)
    {
        _started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}
