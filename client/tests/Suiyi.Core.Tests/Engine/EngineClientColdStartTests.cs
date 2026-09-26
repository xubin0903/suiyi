using System.Net;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

/// <summary>#94：<see cref="EngineClient"/> 按空闲卸载判断方向冷热、冷方向用冷启动超时、超时自动重试一次（/translate 与 /ocr_translate）。</summary>
public sealed class EngineClientColdStartTests : IDisposable
{
    private const string AllModels = "\"opus-mt-en-jap\",\"opus-mt-en-zh\",\"opus-mt-ja-en\",\"opus-mt-tc-big-zh-ja\",\"opus-mt-zh-en\"";

    /// <summary>全部加载、空闲卸载 600 s（#93 默认）。</summary>
    private const string IdleHealth = """
        {"status":"ok","version":"0.0.3","models_dir":"/m","uptime_s":1.0,"ocr_loaded":true,"model_idle_unload_s":600,
         "loaded_models":[
        """ + AllModels + "]}";

    private const string IdleDisabledHealth = """
        {"status":"ok","version":"0.0.3","models_dir":"/m","uptime_s":1.0,"ocr_loaded":true,"model_idle_unload_s":0,
         "loaded_models":[
        """ + AllModels + "]}";

    private const string ZhEnResponse = """{"text":"Hello","source":"zh","detected":false,"target":"en","route":["opus-mt-zh-en"],"elapsed_ms":1}""";
    private const string EnZhResponse = """{"text":"你好","source":"en","detected":false,"target":"zh","route":["opus-mt-en-zh"],"elapsed_ms":1}""";

    private const string OcrZhEnResponse = """
        {"lines":[],"paragraphs":[{"text":"你好","box":[0,0,10,10]}],"text":"你好","image":{"width":10,"height":10},
         "translation":{"results":[{"text":"Hello","source":"zh","detected":true,"target":"en","route":["opus-mt-zh-en"],"elapsed_ms":1}]},
         "elapsed_ms":{"ocr":1,"translate":1,"total":2}}
        """;

    private readonly FakeEngineHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ListLogger _log = new();
    private readonly EngineClient _client;

    public EngineClientColdStartTests()
    {
        _client = new EngineClient(EngineClient.DefaultPort, _handler, _time) { Logger = _log };
    }

    public void Dispose() => _client.Dispose();

    private static HttpResponseMessage Json(string json) => FakeEngineHandler.Json(HttpStatusCode.OK, json);

    private async Task WarmZhEnAsync()
    {
        _handler.Translate = (_, _) => Task.FromResult(Json(ZhEnResponse));
        await _client.TranslateAsync("你好", "zh", "en");
    }

    // ---- /health 字段 ----

    [Fact]
    public async Task Health_ParsesModelIdleUnload_AndInvalidateClearsIt()
    {
        _handler.Health = IdleHealth;
        await _client.GetHealthAsync();
        Assert.Equal(600, _client.KnownModelIdleUnloadSeconds);

        _client.Invalidate();
        Assert.Null(_client.KnownModelIdleUnloadSeconds);
    }

    [Fact]
    public async Task Health_OldEngineWithoutField_IsNull()
    {
        _handler.Health = FakeEngineHandler.AllLoadedHealth;
        await _client.GetHealthAsync();
        Assert.Null(_client.KnownModelIdleUnloadSeconds);
    }

    // ---- 冷热判断 → 超时选择（/translate） ----

    [Fact]
    public async Task Translate_FirstUseOfDirection_UsesColdTimeoutEvenIfLoaded()
    {
        _handler.Health = IdleHealth;
        await _client.GetHealthAsync();
        await _client.GetLanguagesAsync();

        // loaded_models 里有，但本客户端从没用过：可能已被卸载 → 冷启动超时。
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.LazyLoadMs), _client.GetTimeout("你好", "zh", "en"));
    }

    [Fact]
    public async Task Translate_WarmWithinIdleWindow_ShortTimeout_ThenColdAfterThreshold()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs), _client.GetTimeout("你好", "zh", "en"));

        _time.Advance(TimeSpan.FromSeconds(600 - ColdStartRule.SafetyMarginSeconds) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs), _client.GetTimeout("你好", "zh", "en"));

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.LazyLoadMs), _client.GetTimeout("你好", "zh", "en"));
    }

    [Fact]
    public async Task Translate_DirectionsTrackedIndependently()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();

        // zh→en 热，en→zh 从没用过 → 冷。
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs), _client.GetTimeout("你好", "zh", "en"));
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.LazyLoadMs), _client.GetTimeout("Hello", "en", "zh"));
    }

    [Fact]
    public async Task Translate_PivotRoute_ColdIfAnyModelCold()
    {
        _handler.Health = IdleHealth;
        _handler.Translate = (_, _) => Task.FromResult(Json(EnZhResponse));
        await _client.TranslateAsync("Hello", "en", "zh");

        // ja→zh 走 ja-en + en-zh：en-zh 热，ja-en 没用过 → 冷（ja→zh 本来就是段落档，冷时为 10000）。
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.LazyLoadMs), _client.GetTimeout("こんにちは", "ja", "zh"));
    }

    [Fact]
    public async Task Translate_OldEngineWithoutField_KeepsOldLogic()
    {
        _handler.Health = FakeEngineHandler.AllLoadedHealth;
        await _client.GetHealthAsync();
        await _client.GetLanguagesAsync();

        // 从没用过也按 loaded_models：1500，且过多久都不变。
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs), _client.GetTimeout("你好", "zh", "en"));
        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs), _client.GetTimeout("你好", "zh", "en"));
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task Translate_IdleUnloadDisabled_KeepsOldLogic()
    {
        _handler.Health = IdleDisabledHealth;
        await _client.GetHealthAsync();
        await _client.GetLanguagesAsync();

        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs), _client.GetTimeout("你好", "zh", "en"));
        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs), _client.GetTimeout("你好", "zh", "en"));
    }

    [Fact]
    public async Task Invalidate_ForgetsUsage_DirectionColdAgain()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs), _client.GetTimeout("你好", "zh", "en"));

        _client.Invalidate(); // 服务重启
        await _client.GetHealthAsync();
        await _client.GetLanguagesAsync();

        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.LazyLoadMs), _client.GetTimeout("你好", "zh", "en"));
    }

    [Fact]
    public async Task Translate_ColdDirection_ActuallyWaitsColdTimeout_AndLogsWithoutText()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        _time.Advance(TimeSpan.FromMinutes(10)); // 空闲 10 分钟
        var hang = new HangingRequests { Respond = _ => null };
        _handler.Translate = hang.Handle;

        var task = _client.TranslateAsync("机密原文", "zh", "en");
        await hang.Arrived(1);
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs));
        await Task.Yield();
        Assert.False(task.IsCompleted, "冷方向不应按 1500 ms 短超时");
        Assert.Equal(1, hang.Count);

        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.LazyLoadMs - TimeoutPolicy.ShortDirectMs));
        await hang.Arrived(2); // 冷超时到了才重试
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.LazyLoadMs));
        await Assert.ThrowsAsync<EngineException>(() => task);

        var cold = Assert.Single(_log.Lines, line => line.Contains("opus-mt-zh-en=冷(600s)", StringComparison.Ordinal));
        Assert.Contains("翻译：zh→en 的模型可能已被空闲卸载（model_idle_unload_s=600", cold, StringComparison.Ordinal);
        Assert.Contains("按冷启动超时 10000 ms（原 1500 ms）", cold, StringComparison.Ordinal);
        Assert.Single(_log.Lines, line => line.Contains("自动重试一次", StringComparison.Ordinal));
        Assert.All(_log.Lines, line => Assert.DoesNotContain("机密原文", line, StringComparison.Ordinal));
    }

    // ---- 超时重试（/translate） ----

    [Fact]
    public async Task Translate_TimeoutThenSuccess_ReturnsResultAfterOneRetry()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        var hang = new HangingRequests { Respond = n => n == 2 ? Json(ZhEnResponse) : null };
        _handler.Translate = hang.Handle;

        var task = _client.TranslateAsync("你好", "zh", "en");
        await hang.Arrived(1);
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs));

        var result = await task;

        Assert.Equal("Hello", result.Text);
        Assert.Equal(2, hang.Count);
        var line = Assert.Single(_log.Lines, l => l.Contains("自动重试一次", StringComparison.Ordinal));
        Assert.Contains("/translate zh→en 1500 ms 超时，按冷启动超时 10000 ms", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Translate_RetryUsesColdTimeout_ThenFailsWithoutThirdAttempt()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        var hang = new HangingRequests();
        _handler.Translate = hang.Handle;

        var task = _client.TranslateAsync("你好", "zh", "en");
        await hang.Arrived(1);
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs));
        await hang.Arrived(2);

        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.LazyLoadMs - 1));
        await Task.Yield();
        Assert.False(task.IsCompleted, "重试应按冷启动超时 10000 ms");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        var ex = await Assert.ThrowsAsync<EngineException>(() => task);
        Assert.Equal(EngineErrorKind.Timeout, ex.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.LazyLoadMs), ex.Timeout);

        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();
        Assert.Equal(2, hang.Count);
        Assert.Equal(2, _handler.TranslateRequests.Count - 1); // 减去 WarmZhEnAsync 的一次
    }

    [Fact]
    public async Task Translate_CallerCancelsFirstAttempt_NoRetry()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        var hang = new HangingRequests();
        _handler.Translate = hang.Handle;
        using var cts = new CancellationTokenSource();

        var task = _client.TranslateAsync("你好", "zh", "en", cts.Token);
        await hang.Arrived(1);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();
        Assert.Equal(1, hang.Count);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("自动重试", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Translate_CallerCancelsDuringRetry_ThrowsCanceled()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        var hang = new HangingRequests();
        _handler.Translate = hang.Handle;
        using var cts = new CancellationTokenSource();

        var task = _client.TranslateAsync("你好", "zh", "en", cts.Token);
        await hang.Arrived(1);
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs));
        await hang.Arrived(2);
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Equal(2, hang.Count);
    }

    [Fact]
    public async Task Translate_NonTimeoutError_NotRetried()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        var calls = 0;
        _handler.Translate = (_, _) =>
        {
            calls++;
            return Task.FromResult(FakeEngineHandler.Json(500, """{"error":{"code":"internal_error","message":"boom","details":{}}}"""));
        };

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.TranslateAsync("你好", "zh", "en"));

        Assert.Equal(EngineErrorKind.Internal, ex.Kind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Translate_OldEngine_TimeoutStillRetriedOnce()
    {
        // 老引擎（没有 model_idle_unload_s）：超时选择按老逻辑，但超时后同样自动重试一次。
        _handler.Health = FakeEngineHandler.AllLoadedHealth;
        var hang = new HangingRequests { Respond = n => n == 2 ? Json(ZhEnResponse) : null };
        _handler.Translate = hang.Handle;

        var task = _client.TranslateAsync("你好", "zh", "en");
        await hang.Arrived(1);
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs));

        Assert.Equal("Hello", (await task).Text);
        Assert.Equal(2, hang.Count);
    }

    // ---- /ocr_translate ----

    [Fact]
    public async Task OcrTranslate_ColdTranslationModel_UsesOcrColdTimeout_WarmAfterSuccess()
    {
        _handler.Health = IdleHealth;
        await _client.GetHealthAsync();
        await _client.GetLanguagesAsync();

        // OCR 已加载、翻译模型在 loaded_models 里，但从没用过 → 按冷处理（30000）。
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrColdMs), _client.GetOcrTranslateTimeout("zh", "en", null));

        _handler.OcrTranslate = (_, _) => Task.FromResult(Json(OcrZhEnResponse));
        await _client.OcrTranslateAsync(TestPng.Small, "zh", "en", null);
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrMs), _client.GetOcrTranslateTimeout("zh", "en", null));

        // 框选翻译用过的模型，复制翻译也算热（引擎按模型计时）。
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.ShortDirectMs), _client.GetTimeout("你好", "zh", "en"));

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrColdMs), _client.GetOcrTranslateTimeout("zh", "en", null));
    }

    [Fact]
    public async Task OcrTranslate_OldEngine_KeepsOldLogic()
    {
        _handler.Health = FakeEngineHandler.AllLoadedHealth.Replace("\"uptime_s\":1.0,", "\"uptime_s\":1.0,\"ocr_loaded\":true,", StringComparison.Ordinal);
        await _client.GetHealthAsync();
        await _client.GetLanguagesAsync();

        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrMs), _client.GetOcrTranslateTimeout("zh", "en", null));
    }

    [Fact]
    public async Task OcrTranslate_TimeoutThenSuccess_RetriesOnceWithOcrColdTimeout()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        var hang = new HangingRequests { Respond = n => n == 2 ? Json(OcrZhEnResponse) : null };
        _handler.OcrTranslate = hang.HandleRecorded;

        var task = _client.OcrTranslateAsync(TestPng.Small, "zh", "en", null);
        await hang.Arrived(1);
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrMs));

        var result = await task;

        Assert.Equal("Hello", result.Translation!.Results[0].Text);
        Assert.Equal(2, hang.Count);
        var requests = _handler.OcrRequests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].Query, requests[1].Query);
        Assert.Equal(requests[0].BodyBytes, requests[1].BodyBytes); // 重发同一张 PNG
        Assert.Contains(_log.Lines, l => l.Contains("/ocr_translate zh→en 15000 ms 超时，按冷启动超时 30000 ms 自动重试一次", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OcrTranslate_TimesOutTwice_FailsAfterExactlyTwoAttempts()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        var hang = new HangingRequests();
        _handler.OcrTranslate = hang.HandleRecorded;

        var task = _client.OcrTranslateAsync(TestPng.Small, "zh", "en", null);
        await hang.Arrived(1);
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrMs));
        await hang.Arrived(2);
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrColdMs));

        var ex = await Assert.ThrowsAsync<EngineException>(() => task);
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrColdMs), ex.Timeout);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();
        Assert.Equal(2, hang.Count);
    }

    [Fact]
    public async Task OcrTranslate_CallerCancels_NoRetry()
    {
        _handler.Health = IdleHealth;
        await WarmZhEnAsync();
        var hang = new HangingRequests();
        _handler.OcrTranslate = hang.HandleRecorded;
        using var cts = new CancellationTokenSource();

        var task = _client.OcrTranslateAsync(TestPng.Small, "zh", "en", null, cts.Token);
        await hang.Arrived(1);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();
        Assert.Equal(1, hang.Count);
    }
}
