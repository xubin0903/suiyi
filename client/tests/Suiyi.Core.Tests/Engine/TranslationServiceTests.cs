using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

public sealed class TranslationServiceTests : IDisposable
{
    private readonly FakeEngineHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly EngineClient _client;
    private (string Primary, string Secondary) _targets = ("zh", "en");

    public TranslationServiceTests()
    {
        _client = new EngineClient(EngineClient.DefaultPort, _handler, _time);
    }

    public void Dispose() => _client.Dispose();

    private TranslationService CreateService() => new(_client, () => _targets, _time);

    private static (string Text, string Source, string Target) Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return (root.GetProperty("text").GetString()!, root.GetProperty("source").GetString()!, root.GetProperty("target").GetString()!);
    }

    /// <summary>模拟服务：auto 时按文字判断语种（含汉字 → zh，含假名 → ja，否则 en）。</summary>
    private static Task<HttpResponseMessage> FakeServer(string body, CancellationToken cancellationToken)
    {
        var (text, source, target) = Parse(body);
        var detected = source == "auto";
        var actual = detected
            ? text.Any(c => c is >= '\u3040' and <= '\u30ff') ? "ja" : text.Any(c => c is >= '\u4e00' and <= '\u9fff') ? "zh" : "en"
            : source;
        var route = actual == target ? "[]" : $"[\"opus-mt-{actual}-{target}\"]";
        var translated = actual == target ? text : $"[{actual}->{target}] {text}";
        var json = JsonSerializer.Serialize(new
        {
            text = translated,
            source = actual,
            detected,
            target,
            elapsed_ms = 10.0,
        });
        json = json.Insert(json.Length - 1, $",\"route\":{route}");
        return Task.FromResult(FakeEngineHandler.Json(HttpStatusCode.OK, json));
    }

    [Fact]
    public async Task TranslateAsync_ForeignText_TranslatesToPrimary()
    {
        _handler.Translate = FakeServer;
        using var service = CreateService();

        var outcome = await service.TranslateAsync("Hello");

        Assert.Equal("[en->zh] Hello", outcome.Text);
        Assert.Equal("en", outcome.SourceLanguage);
        Assert.True(outcome.SourceDetected);
        Assert.Equal("zh", outcome.TargetLanguage);
        Assert.False(outcome.Retargeted);
        Assert.Equal(["opus-mt-en-zh"], outcome.Route);
        Assert.Equal(10.0, outcome.ServerElapsedMs);
        Assert.Equal(1, outcome.RequestCount);
        Assert.Equal(("Hello", "auto", "zh"), Parse(Assert.Single(_handler.TranslateRequests).Body!));
    }

    [Fact]
    public async Task TranslateAsync_TextAlreadyInPrimary_RetargetsToSecondary()
    {
        _handler.Translate = FakeServer;
        using var service = CreateService();

        var outcome = await service.TranslateAsync("你好");

        Assert.Equal("[zh->en] 你好", outcome.Text);
        Assert.Equal("zh", outcome.SourceLanguage);
        Assert.True(outcome.SourceDetected);
        Assert.Equal("en", outcome.TargetLanguage);
        Assert.True(outcome.Retargeted);
        Assert.Equal(["opus-mt-zh-en"], outcome.Route);
        Assert.Equal(20.0, outcome.ServerElapsedMs);
        Assert.Equal(2, outcome.RequestCount);
        Assert.Equal(
            [("你好", "auto", "zh"), ("你好", "zh", "en")],
            _handler.TranslateRequests.Select(r => Parse(r.Body!)));
    }

    [Fact]
    public async Task TranslateAsync_ReadsTargetsOnEveryCall()
    {
        _handler.Translate = FakeServer;
        using var service = CreateService();

        await service.TranslateAsync("Hello");
        _targets = ("ja", "zh");
        var outcome = await service.TranslateAsync("Hello");

        Assert.Equal("ja", outcome.TargetLanguage);
    }

    [Fact]
    public async Task TranslateAsync_SourceOverride_SkipsDetection()
    {
        _handler.Translate = FakeServer;
        using var service = CreateService();

        // 纯汉字日语会被检测成 zh，用户手动指定 ja。
        var outcome = await service.TranslateAsync("東京駅", sourceOverride: "ja");

        Assert.Equal(("東京駅", "ja", "zh"), Parse(Assert.Single(_handler.TranslateRequests).Body!));
        Assert.Equal("ja", outcome.SourceLanguage);
        Assert.False(outcome.SourceDetected);
        Assert.Equal("zh", outcome.TargetLanguage);
        Assert.False(outcome.Retargeted);
    }

    [Fact]
    public async Task TranslateAsync_SourceOverrideEqualsPrimary_GoesStraightToSecondary()
    {
        _handler.Translate = FakeServer;
        using var service = CreateService();

        var outcome = await service.TranslateAsync("你好", sourceOverride: "zh");

        Assert.Equal(("你好", "zh", "en"), Parse(Assert.Single(_handler.TranslateRequests).Body!));
        Assert.True(outcome.Retargeted);
        Assert.False(outcome.SourceDetected);
        Assert.Equal("en", outcome.TargetLanguage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("zh")]
    public async Task TranslateAsync_NoUsableSecondary_ReturnsTextUnchanged(string secondary)
    {
        _handler.Translate = FakeServer;
        _targets = ("zh", secondary);
        using var service = CreateService();

        var outcome = await service.TranslateAsync("你好");

        Assert.Equal("你好", outcome.Text);
        Assert.Empty(outcome.Route);
        Assert.False(outcome.Retargeted);
        Assert.Single(_handler.TranslateRequests);
    }

    [Fact]
    public async Task TranslateAsync_MeasuresClientRoundTripWithTimeProvider()
    {
        _handler.Translate = (body, ct) =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            return FakeServer(body, ct);
        };
        using var service = CreateService();

        var outcome = await service.TranslateAsync("你好"); // 改译：两次请求

        Assert.Equal(TimeSpan.FromMilliseconds(500), outcome.ClientElapsed);
    }

    [Fact]
    public async Task TranslateAsync_NewCallCancelsPrevious()
    {
        var hang = new HangingTranslate();
        _handler.Translate = hang.Handle;
        using var service = CreateService();

        var first = service.TranslateAsync("Hello");
        await hang.Started;
        _handler.Translate = FakeServer;
        var second = await service.TranslateAsync("World");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("[en->zh] World", second.Text);
    }

    [Fact]
    public async Task TranslateAsync_SupersededDuringRetarget_DoesNotSendSecondRequest()
    {
        using var service = CreateService();
        var firstRequestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Translate = async (body, ct) =>
        {
            firstRequestSeen.TrySetResult();
            await release.Task; // 不理会取消，模拟响应已在路上
            return await FakeServer(body, ct);
        };

        var first = service.TranslateAsync("你好");
        await firstRequestSeen.Task;
        service.CancelCurrent();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Single(_handler.TranslateRequests);
    }

    [Fact]
    public async Task TranslateAsync_CallerCancels_ThrowsOperationCanceled()
    {
        var hang = new HangingTranslate();
        _handler.Translate = hang.Handle;
        using var service = CreateService();
        using var cts = new CancellationTokenSource();

        var task = service.TranslateAsync("Hello", cancellationToken: cts.Token);
        await hang.Started;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task TranslateAsync_EngineError_Propagates()
    {
        _handler.Translate = (_, _) => Task.FromResult(FakeEngineHandler.Json(422, """
            {"error":{"code":"detect_failed","message":"","details":{"index":0,"detected":"und"}}}
            """));
        using var service = CreateService();

        var ex = await Assert.ThrowsAsync<EngineException>(() => service.TranslateAsync("!!!"));

        Assert.Equal(EngineErrorKind.DetectFailed, ex.Kind);
    }

    [Fact]
    public async Task TranslateAsync_RetargetUnsupported_PropagatesUnsupportedPair()
    {
        _handler.Translate = (body, ct) => Parse(body).Source == "auto"
            ? FakeServer(body, ct)
            : Task.FromResult(FakeEngineHandler.Json(422, """
                {"error":{"code":"unsupported_pair","message":"","details":{"source":"zh","target":"en","missing_models":["opus-mt-zh-en"]}}}
                """));
        using var service = CreateService();

        var ex = await Assert.ThrowsAsync<EngineException>(() => service.TranslateAsync("你好"));

        Assert.Equal(EngineErrorKind.UnsupportedPair, ex.Kind);
        Assert.Equal("未安装语向模型：opus-mt-zh-en", ex.UserMessage);
    }

    [Fact]
    public async Task TranslateAsync_AfterDispose_Throws()
    {
        var service = CreateService();
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TranslateAsync("Hello"));
    }
}
