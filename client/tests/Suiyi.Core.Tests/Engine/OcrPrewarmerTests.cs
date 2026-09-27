using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

/// <summary><see cref="OcrPrewarmer"/>（#109）：框选前后台预热 OCR 子进程。</summary>
public sealed class OcrPrewarmerTests : IDisposable
{
    private readonly FakeEngineHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ListLogger _logger = new();
    private readonly EngineClient _client;
    private readonly OcrPrewarmer _prewarmer;

    public OcrPrewarmerTests()
    {
        _client = new EngineClient(EngineClient.DefaultPort, _handler, _time);
        _prewarmer = new OcrPrewarmer(_client, _logger, _time);
        _handler.Health = Health("stopped");
    }

    public void Dispose()
    {
        _prewarmer.Dispose();
        _client.Dispose();
    }

    /// <summary>#104 引擎的 <c>/health</c>；<paramref name="state"/> 为 <see langword="null"/> 时模拟老引擎（没有 worker 字段）。</summary>
    internal static string Health(string? state, bool? ocrLoaded = false, string? errorReason = null, int? pid = null)
    {
        var worker = state is null
            ? string.Empty
            : $",\"ocr_worker_state\":\"{state}\",\"ocr_worker_pid\":{(pid is null ? "null" : pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
        var loaded = ocrLoaded is null ? string.Empty : $",\"ocr_loaded\":{(ocrLoaded.Value ? "true" : "false")}";
        var error = errorReason is null ? string.Empty : $",\"ocr_error\":{{\"code\":\"ocr_unavailable\",\"reason\":\"{errorReason}\",\"message\":\"x\"}}";
        return "{\"status\":\"ok\",\"version\":\"0.0.3\",\"models_dir\":\"/m\",\"uptime_s\":1.0,\"loaded_models\":[]"
            + loaded + error + worker + "}";
    }

    private static HttpResponseMessage OcrOk() =>
        FakeEngineHandler.Json(HttpStatusCode.OK, """{"lines":[],"paragraphs":[],"text":"","image":{"width":32,"height":32},"elapsed_ms":5}""");

    private IReadOnlyList<RecordedRequest> OcrOnly => [.. _handler.Requests.Where(r => r.Path == "/ocr")];

    private async Task<OcrPrewarmStart> PrewarmAndWaitAsync()
    {
        var start = _prewarmer.Prewarm();
        await _prewarmer.LastRun.WaitAsync(TimeSpan.FromSeconds(5));
        return start;
    }

    // ---- 预热图片 ----

    [Fact]
    public void BlankPng_Is32x32Png_AndPassesPrecheck()
    {
        Assert.True(EngineClient.TryReadPngSize(OcrPrewarmer.BlankPng.Span, out var width, out var height));
        Assert.Equal(32, width);
        Assert.Equal(32, height);
        Assert.Null(EngineClient.PrecheckImage(OcrPrewarmer.BlankPng.Span));
        Assert.True(OcrPrewarmer.BlankPng.Length < 128);
    }

    // ---- 决策（纯函数） ----

    [Theory]
    [InlineData("stopped", false, null, OcrPrewarmDecision.Prewarm)]
    [InlineData("stopped", null, null, OcrPrewarmDecision.Prewarm)]
    [InlineData("stopped", false, "worker_crashed", OcrPrewarmDecision.Prewarm)] // 崩溃后下次请求会重启子进程，预热有意义
    [InlineData("stopped", false, "worker_timeout", OcrPrewarmDecision.Prewarm)]
    [InlineData("stopped", false, "load_failed", OcrPrewarmDecision.Prewarm)]
    [InlineData("starting", false, null, OcrPrewarmDecision.SkipAlreadyWarm)]
    [InlineData("ready", true, null, OcrPrewarmDecision.SkipAlreadyWarm)]
    [InlineData("busy", true, null, OcrPrewarmDecision.SkipAlreadyWarm)]
    [InlineData("in_process", true, null, OcrPrewarmDecision.SkipAlreadyWarm)]
    [InlineData("in_process", false, null, OcrPrewarmDecision.Prewarm)]
    [InlineData("in_process", null, null, OcrPrewarmDecision.Prewarm)]
    [InlineData("stopped", false, "dependency_missing", OcrPrewarmDecision.SkipOcrUnavailable)]
    [InlineData("stopped", false, "models_missing", OcrPrewarmDecision.SkipOcrUnavailable)]
    [InlineData("stopped", false, "models_invalid", OcrPrewarmDecision.SkipOcrUnavailable)]
    [InlineData("stopped", false, "manifest_unavailable", OcrPrewarmDecision.SkipOcrUnavailable)]
    [InlineData("hibernating", false, null, OcrPrewarmDecision.SkipUnknownState)]
    [InlineData(null, false, null, OcrPrewarmDecision.SkipUnsupported)]
    [InlineData(null, null, null, OcrPrewarmDecision.SkipUnsupported)]
    public void Decide_FollowsWorkerStateAndOcrError(string? state, bool? loaded, string? reason, OcrPrewarmDecision expected)
    {
        var health = new HealthResponse
        {
            Status = "ok",
            OcrWorkerState = state,
            OcrLoaded = loaded,
            OcrError = reason is null ? null : new OcrHealthError { Reason = reason },
        };

        Assert.Equal(expected, OcrPrewarmer.Decide(health));
    }

    [Fact]
    public void HealthResponse_ParsesWorkerFields()
    {
        var health = System.Text.Json.JsonSerializer.Deserialize<HealthResponse>(Health("ready", true, pid: 4321))!;
        Assert.Equal("ready", health.OcrWorkerState);
        Assert.Equal(4321, health.OcrWorkerPid);

        var old = System.Text.Json.JsonSerializer.Deserialize<HealthResponse>(Health(null))!;
        Assert.Null(old.OcrWorkerState);
        Assert.Null(old.OcrWorkerPid);
    }

    // ---- 触发、不等结果 ----

    [Fact]
    public async Task Stopped_SendsBlankOcr_AndLogsOneLine()
    {
        _handler.Ocr = (_, _) => Task.FromResult(OcrOk());

        Assert.Equal(OcrPrewarmStart.Started, await PrewarmAndWaitAsync());

        var request = Assert.Single(OcrOnly);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("image/png", request.ContentType);
        Assert.Equal(OcrPrewarmer.BlankPng.ToArray(), request.BodyBytes);
        Assert.Equal(OcrPrewarmDecision.Prewarm, _prewarmer.LastDecision);
        Assert.Equal(1, _prewarmer.RequestsSent);
        Assert.True(_client.KnownOcrLoaded); // 正式请求随后按热超时
        var line = Assert.Single(_logger.Lines);
        Assert.Contains("框选预热", line, StringComparison.Ordinal);
        Assert.Contains("ms", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prewarm_ReturnsImmediately_WhileOcrHangs()
    {
        var hanging = new HangingRequests();
        _handler.Ocr = hanging.HandleRecorded;

        var start = _prewarmer.Prewarm();

        Assert.Equal(OcrPrewarmStart.Started, start);
        await hanging.Arrived(1);
        Assert.False(_prewarmer.LastRun.IsCompleted); // 预热仍在后台，调用方没有等

        _prewarmer.Dispose(); // 退出程序：取消后台请求
        await _prewarmer.LastRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(_logger.Lines); // 取消不写失败日志
    }

    // ---- 失败静默 ----

    [Fact]
    public async Task Timeout_IsSwallowed_AndLaterOcrTranslateWorks()
    {
        var hanging = new HangingRequests();
        _handler.Ocr = hanging.HandleRecorded;

        _prewarmer.Prewarm();
        await hanging.Arrived(1);
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrColdMs + 1));
        await _prewarmer.LastRun.WaitAsync(TimeSpan.FromSeconds(5));

        var line = Assert.Single(_logger.Lines);
        Assert.Contains("框选预热失败（已忽略）", line, StringComparison.Ordinal);
        Assert.Contains("Timeout", line, StringComparison.Ordinal);

        var result = await _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en");
        Assert.NotNull(result);
        Assert.Single(_handler.Requests, r => r.Path == "/ocr_translate");
    }

    [Theory]
    [InlineData(503, """{"error":{"code":"ocr_unavailable","message":"x","details":{"reason":"worker_crashed"}}}""")]
    [InlineData(500, """{"detail":"boom"}""")]
    [InlineData(504, """{"error":{"code":"ocr_unavailable","message":"x","details":{"reason":"worker_timeout"}}}""")]
    public async Task ServerError_IsSwallowed_AndLaterOcrTranslateWorks(int status, string body)
    {
        _handler.Ocr = (_, _) => Task.FromResult(FakeEngineHandler.Json(status, body));

        await PrewarmAndWaitAsync();

        Assert.Single(_logger.Lines, l => l.Contains("框选预热失败（已忽略）", StringComparison.Ordinal));
        await _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en");
        Assert.Single(_handler.Requests, r => r.Path == "/ocr_translate");
    }

    [Fact]
    public async Task ConnectionRefusedOnOcr_IsSwallowed()
    {
        _handler.Ocr = (_, _) => throw new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));

        await PrewarmAndWaitAsync();

        Assert.Single(_logger.Lines, l => l.Contains("框选预热失败（已忽略）", StringComparison.Ordinal));
        Assert.True(_prewarmer.LastRun.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HealthFails_NoOcrRequest_NoLog()
    {
        _handler.Health = "not json";

        await PrewarmAndWaitAsync();

        Assert.Empty(OcrOnly);
        Assert.Null(_prewarmer.LastDecision);
        Assert.Empty(_logger.Lines);
        Assert.True(_prewarmer.LastRun.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task UnexpectedException_IsSwallowed()
    {
        _handler.Ocr = (_, _) => throw new InvalidOperationException("bug");

        await PrewarmAndWaitAsync();

        Assert.True(_prewarmer.LastRun.IsCompletedSuccessfully);
        Assert.Single(_logger.Lines, l => l.Contains("框选预热失败（已忽略）", StringComparison.Ordinal));
    }

    // ---- 跳过 ----

    [Theory]
    [InlineData("ready")]
    [InlineData("busy")]
    [InlineData("starting")]
    public async Task AlreadyWarm_Skips_NoLog(string state)
    {
        _handler.Health = Health(state, ocrLoaded: state != "starting", pid: 42);

        await PrewarmAndWaitAsync();

        Assert.Empty(OcrOnly);
        Assert.Equal(OcrPrewarmDecision.SkipAlreadyWarm, _prewarmer.LastDecision);
        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public async Task OldEngineWithoutWorkerState_Skips()
    {
        _handler.Health = Health(null, ocrLoaded: false);

        await PrewarmAndWaitAsync();

        Assert.Empty(OcrOnly);
        Assert.Equal(OcrPrewarmDecision.SkipUnsupported, _prewarmer.LastDecision);
        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public async Task OcrModelsMissing_Skips()
    {
        _handler.Health = Health("stopped", errorReason: "models_missing");

        await PrewarmAndWaitAsync();

        Assert.Empty(OcrOnly);
        Assert.Equal(OcrPrewarmDecision.SkipOcrUnavailable, _prewarmer.LastDecision);
    }

    // ---- 节流 ----

    [Fact]
    public async Task RepeatWhileInFlight_DoesNotStartAnother()
    {
        var hanging = new HangingRequests();
        _handler.Ocr = hanging.HandleRecorded;

        Assert.Equal(OcrPrewarmStart.Started, _prewarmer.Prewarm());
        await hanging.Arrived(1);
        _time.Advance(TimeSpan.FromSeconds(10)); // 冷却已过，但上一次还在进行
        Assert.Equal(OcrPrewarmStart.SkippedInFlight, _prewarmer.Prewarm());
        Assert.Equal(1, hanging.Count);

        _prewarmer.Dispose();
        await _prewarmer.LastRun.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RepeatWithinCooldown_Skips_ThenAllowedAfterCooldown()
    {
        _handler.Ocr = (_, _) => Task.FromResult(OcrOk());

        await PrewarmAndWaitAsync();
        _time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Equal(OcrPrewarmStart.SkippedCooldown, _prewarmer.Prewarm());
        Assert.Single(OcrOnly);

        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(OcrPrewarmStart.Started, await PrewarmAndWaitAsync());
        Assert.Equal(2, _handler.Requests.Count(r => r.Path == "/health"));
        Assert.Equal(2, OcrOnly.Count); // Health 仍是 stopped（假引擎），所以再次预热
    }

    [Fact]
    public async Task RepeatAfterCooldown_WhenHealthSaysReady_OnlyChecksHealth()
    {
        _handler.Ocr = (_, _) =>
        {
            _handler.Health = Health("ready", ocrLoaded: true, pid: 7);
            return Task.FromResult(OcrOk());
        };

        await PrewarmAndWaitAsync();
        _time.Advance(TimeSpan.FromSeconds(6));
        await PrewarmAndWaitAsync();

        Assert.Single(OcrOnly);
        Assert.Equal(OcrPrewarmDecision.SkipAlreadyWarm, _prewarmer.LastDecision);
        Assert.Single(_logger.Lines);
    }

    [Fact]
    public void AfterDispose_ReturnsDisposed_AndSendsNothing()
    {
        _prewarmer.Dispose();

        Assert.Equal(OcrPrewarmStart.Disposed, _prewarmer.Prewarm());
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void DefaultCooldown_IsFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), OcrPrewarmer.DefaultCooldown);
        Assert.Equal(OcrPrewarmer.DefaultCooldown, _prewarmer.Cooldown);
    }
}
