using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

public sealed class EngineClientTests : IDisposable
{
    private readonly FakeEngineHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly EngineClient _client;

    public EngineClientTests()
    {
        _client = new EngineClient(EngineClient.DefaultPort, _handler, _time);
    }

    public void Dispose() => _client.Dispose();

    private static Task<HttpResponseMessage> Ok(string json) =>
        Task.FromResult(FakeEngineHandler.Json(HttpStatusCode.OK, json));

    // ---- 基本请求 ----

    [Fact]
    public void BaseAddress_IsLoopbackWithPort()
    {
        using var client = new EngineClient(12345, _handler);
        Assert.Equal(new Uri("http://127.0.0.1:12345/"), client.BaseAddress);
    }

    [Fact]
    public void CreateDefaultHandler_DisablesProxy()
    {
        using var handler = EngineClient.CreateDefaultHandler();
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task TranslateAsync_Normal_ReturnsResponseAndPostsUtf8Json()
    {
        _handler.Translate = (_, _) => Ok("""
            {"text":"Hello, world","source":"zh","detected":false,"target":"en","route":["opus-mt-zh-en"],"elapsed_ms":42.5}
            """);

        var result = await _client.TranslateAsync("你好，世界", "zh", "en");

        Assert.Equal("Hello, world", result.Text);
        Assert.Equal("zh", result.Source);
        Assert.False(result.Detected);
        Assert.Equal("en", result.Target);
        Assert.Equal(["opus-mt-zh-en"], result.Route);
        Assert.Equal(42.5, result.ElapsedMs);

        var request = Assert.Single(_handler.TranslateRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("utf-8", request.CharSet);
        using var doc = JsonDocument.Parse(request.Body!);
        Assert.Equal("你好，世界", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("zh", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("en", doc.RootElement.GetProperty("target").GetString());
    }

    [Fact]
    public async Task TranslateAsync_TargetOnlyOverload_SendsAuto()
    {
        await _client.TranslateAsync("Hello", "zh");

        using var doc = JsonDocument.Parse(Assert.Single(_handler.TranslateRequests).Body!);
        Assert.Equal("auto", doc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Responses_IgnoreUnknownFields()
    {
        _handler.Health = """{"status":"ok","version":"0.0.9","models_dir":"/m","loaded_models":["a"],"uptime_s":3,"gpu":{"x":1},"new":[1,2]}""";
        _handler.Languages = """{"languages":["en","zh"],"pairs":[{"src":"en","tgt":"zh","route":"direct","models":["m"],"quality":0.9}],"extra":true}""";
        _handler.Translate = (_, _) => Ok("""
            {"text":"你好","source":"en","detected":true,"target":"zh","route":["m"],"elapsed_ms":1,"confidence":0.99,"glossary":{"hits":[]}}
            """);

        var health = await _client.GetHealthAsync();
        var languages = await _client.GetLanguagesAsync();
        var result = await _client.TranslateAsync("Hello", "zh");

        Assert.Equal("0.0.9", health.Version);
        Assert.Equal(["a"], health.LoadedModels);
        Assert.Equal("m", Assert.Single(languages.Pairs).Models[0]);
        Assert.Equal("你好", result.Text);
    }

    [Fact]
    public async Task GetLanguagesAsync_CachesUntilInvalidate()
    {
        await _client.GetLanguagesAsync();
        await _client.GetLanguagesAsync();
        Assert.Single(_handler.Requests, r => r.Path == "/languages");
        Assert.NotNull(_client.CachedLanguages);

        _client.Invalidate();
        Assert.Null(_client.CachedLanguages);
        Assert.Null(_client.KnownLoadedModels);

        await _client.GetLanguagesAsync();
        Assert.Equal(2, _handler.Requests.Count(r => r.Path == "/languages"));
    }

    [Fact]
    public async Task TranslateAsync_WarmsCachesOnce()
    {
        await _client.TranslateAsync("a", "en", "zh");
        await _client.TranslateAsync("b", "en", "zh");

        Assert.Equal(["/languages", "/health", "/translate", "/translate"], _handler.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task TranslateAsync_RecordsRouteModelsAsLoaded()
    {
        _handler.Health = FakeEngineHandler.NothingLoadedHealth;
        _handler.Translate = (_, _) => Ok("""{"text":"hi","source":"zh","detected":false,"target":"en","route":["opus-mt-zh-en"],"elapsed_ms":1}""");

        Assert.Equal(TimeSpan.FromMilliseconds(10000), _client.GetTimeout("你好", "zh", "en"));
        await _client.TranslateAsync("你好", "zh", "en");

        Assert.Contains("opus-mt-zh-en", _client.KnownLoadedModels!);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), _client.GetTimeout("你好", "zh", "en"));
    }

    // ---- 超时：1500 / 3000 / 10000 / 超长加时 ----

    [Theory]
    [InlineData("你好", "zh", "en", FakeEngineHandler.AllLoadedHealth, 1500)]
    [InlineData("第一行\n第二行", "zh", "en", FakeEngineHandler.AllLoadedHealth, 3000)]
    [InlineData("こんにちは", "ja", "zh", FakeEngineHandler.AllLoadedHealth, 3000)]
    [InlineData("你好", "zh", "en", FakeEngineHandler.NothingLoadedHealth, 10000)]
    public async Task TranslateAsync_TimesOutExactlyAtPolicyValue(string text, string source, string target, string health, int expectedMs)
    {
        _handler.Health = health;
        await AssertTimesOutAt(text, source, target, expectedMs);
    }

    [Fact]
    public async Task TranslateAsync_LongText_GetsExtraTime()
    {
        await AssertTimesOutAt(new string('a', 2500), "en", "zh", 3500);
    }

    private async Task AssertTimesOutAt(string text, string source, string target, int expectedMs)
    {
        var hang = new HangingRequests();
        _handler.Translate = hang.Handle;

        var task = _client.TranslateAsync(text, source, target);
        await hang.Arrived(1);

        _time.Advance(TimeSpan.FromMilliseconds(expectedMs - 1));
        await Task.Yield();
        Assert.False(task.IsCompleted, $"在 {expectedMs - 1} ms 时不应超时");

        // #94：第一次超时后按冷启动超时自动重试一次，仍超时才报错。
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await hang.Arrived(2);
        Assert.False(task.IsCompleted, "第一次超时后应自动重试，而不是立即报错");

        var coldMs = TimeoutPolicy.ComputeColdMilliseconds(text);
        _time.Advance(TimeSpan.FromMilliseconds(coldMs));
        var ex = await Assert.ThrowsAsync<EngineException>(() => task);
        Assert.Equal(EngineErrorKind.Timeout, ex.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(coldMs), ex.Timeout);
        Assert.Contains(coldMs.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.UserMessage, StringComparison.Ordinal);
        Assert.Equal(2, _handler.TranslateRequests.Count);
    }

    [Fact]
    public async Task GetHealthAsync_Timeout_IsTimeoutError()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new EngineClient(EngineClient.DefaultPort, new DelegatingFake(async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        }), _time);

        var task = client.GetHealthAsync();
        await started.Task;
        _time.Advance(TimeSpan.FromMilliseconds(EngineClient.HealthTimeoutMs));

        var ex = await Assert.ThrowsAsync<EngineException>(() => task);
        Assert.Equal(EngineErrorKind.Timeout, ex.Kind);
    }

    // ---- 取消与连接失败 ----

    [Fact]
    public async Task TranslateAsync_CallerCancels_ThrowsOperationCanceledNotEngineError()
    {
        var hang = new HangingTranslate();
        _handler.Translate = hang.Handle;
        using var cts = new CancellationTokenSource();

        var task = _client.TranslateAsync("你好", "zh", "en", cts.Token);
        await hang.Started;
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.IsNotType<EngineException>(ex.InnerException);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task TranslateAsync_ConnectionRefused_IsUnavailable()
    {
        using var client = new EngineClient(EngineClient.DefaultPort, new DelegatingFake(_ => throw new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Connection refused (127.0.0.1:18780)",
            new SocketException((int)SocketError.ConnectionRefused))), _time);

        var ex = await Assert.ThrowsAsync<EngineException>(() => client.TranslateAsync("你好", "zh", "en"));

        Assert.Equal(EngineErrorKind.Unavailable, ex.Kind);
        Assert.Equal("翻译服务未运行或已退出", ex.UserMessage);
    }

    [Fact]
    public async Task TranslateAsync_ConnectionResetWhileReading_IsUnavailable()
    {
        _handler.Translate = (_, _) => throw new IOException("connection reset");

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.TranslateAsync("你好", "zh", "en"));

        Assert.Equal(EngineErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task TranslateAsync_WarmupFailsWithUnknown_StillTranslatesWithLazyTimeout()
    {
        _handler.Languages = "not json";

        var result = await _client.TranslateAsync("Hello", "zh");

        Assert.Equal("en", result.Source);
        Assert.Null(_client.CachedLanguages);
        Assert.Equal(TimeSpan.FromMilliseconds(10000), _client.GetTimeout("Hello", "auto", "zh"));
    }

    // ---- 错误码映射 ----

    [Fact]
    public async Task TranslateAsync_UnsupportedPair_CarriesMissingModels()
    {
        _handler.Translate = (_, _) => Task.FromResult(FakeEngineHandler.Json(422, """
            {"error":{"code":"unsupported_pair","message":"没有模型","details":{"source":"en","target":"zh","missing_models":["opus-mt-en-zh"],"hint":"x"}}}
            """));

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.TranslateAsync("Hello", "en", "zh"));

        Assert.Equal(EngineErrorKind.UnsupportedPair, ex.Kind);
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("unsupported_pair", ex.ErrorCode);
        Assert.Equal(["opus-mt-en-zh"], ex.MissingModels);
        Assert.Equal(("en", "zh"), (ex.SourceLanguage, ex.TargetLanguage));
        Assert.Equal("未安装语向模型：opus-mt-en-zh", ex.UserMessage);
    }

    [Fact]
    public void MapError_UnsupportedPairWithoutMissingModels_ShowsPair()
    {
        var ex = EngineClient.MapError(422, """{"error":{"code":"unsupported_pair","message":"","details":{"source":"ko","target":"zh","missing_models":[]}}}""", "translate");

        Assert.Empty(ex.MissingModels);
        Assert.Equal("不支持该语向：ko→zh", ex.UserMessage);
    }

    [Fact]
    public void MapError_UnsupportedPairSeveralModels_JoinsThem()
    {
        var ex = EngineClient.MapError(422, """{"error":{"code":"unsupported_pair","message":"","details":{"missing_models":["opus-mt-ja-en","opus-mt-en-zh"]}}}""", "translate");

        Assert.Equal("未安装语向模型：opus-mt-ja-en、opus-mt-en-zh", ex.UserMessage);
    }

    [Fact]
    public void MapError_TextTooLong_CarriesLimitAndLength()
    {
        var ex = EngineClient.MapError(413, """{"error":{"code":"text_too_long","message":"","details":{"limit":20000,"length":20001}}}""", "translate");

        Assert.Equal(EngineErrorKind.TextTooLong, ex.Kind);
        Assert.Equal(20000, ex.Limit);
        Assert.Equal(20001, ex.Length);
        Assert.Equal("文本过长：20001 字，上限 20000 字", ex.UserMessage);
    }

    [Theory]
    [InlineData(422, """{"error":{"code":"detect_failed","message":"","details":{"index":0,"detected":"und"}}}""", EngineErrorKind.DetectFailed, "无法识别原文语种，请手动指定")]
    [InlineData(422, """{"error":{"code":"invalid_request","message":"","details":{"field":"target"}}}""", EngineErrorKind.InvalidRequest, "翻译请求无效")]
    [InlineData(500, """{"error":{"code":"internal_error","message":"","details":{}}}""", EngineErrorKind.Internal, "翻译服务内部错误")]
    [InlineData(500, "Internal Server Error", EngineErrorKind.Internal, "翻译服务内部错误")]
    [InlineData(502, "", EngineErrorKind.Internal, "翻译服务内部错误")]
    [InlineData(404, """{"detail":"Not Found"}""", EngineErrorKind.Unknown, "翻译服务返回了无法识别的响应")]
    [InlineData(418, "<html>teapot</html>", EngineErrorKind.Unknown, "翻译服务返回了无法识别的响应")]
    [InlineData(422, """{"error":{"code":"brand_new_code","message":"","details":{}}}""", EngineErrorKind.Unknown, "翻译服务返回了无法识别的响应")]
    [InlineData(503, """{"error":{"code":"brand_new_code","message":"","details":{}}}""", EngineErrorKind.Internal, "翻译服务内部错误")]
    public async Task TranslateAsync_ErrorResponses_MapToKinds(int status, string body, EngineErrorKind kind, string userMessage)
    {
        _handler.Translate = (_, _) => Task.FromResult(FakeEngineHandler.Json(status, body));

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.TranslateAsync("Hello", "en", "zh"));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(userMessage, ex.UserMessage);
    }

    [Fact]
    public async Task TranslateAsync_OkWithUnparseableJson_IsUnknown()
    {
        _handler.Translate = (_, _) => Ok("{not json");

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.TranslateAsync("Hello", "en", "zh"));

        Assert.Equal(EngineErrorKind.Unknown, ex.Kind);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    [Fact]
    public void UserMessage_Timeout_WithoutValue_IsGeneric()
    {
        Assert.Equal("翻译超时，请重试", new EngineException(EngineErrorKind.Timeout, "x").UserMessage);
    }

    [Fact]
    public void UserMessage_EveryKindHasChineseText()
    {
        foreach (var kind in Enum.GetValues<EngineErrorKind>())
        {
            var message = new EngineException(kind, "x").UserMessage;
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.Contains(message, c => c >= '\u4e00' && c <= '\u9fff');
        }
    }

    private sealed class DelegatingFake(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(cancellationToken);
    }
}
