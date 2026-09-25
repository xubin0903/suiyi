using System.Net;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Engine;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Tests.Engine;

public sealed class OcrTranslationServiceTests : IDisposable
{
    private const string SecretText = "机密合同第七条";

    private readonly FakeEngineHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingLogger _logger = new();
    private readonly EngineClient _client;
    private (string Primary, string Secondary) _targets = ("zh", "en");

    public OcrTranslationServiceTests()
    {
        _client = new EngineClient(EngineClient.DefaultPort, _handler, _time);
    }

    public void Dispose() => _client.Dispose();

    private OcrTranslationService CreateService() => new(_client, () => _targets, _logger, _time);

    private static string Query(string key, RecordedRequest request) =>
        request.Query.Split('&').Select(p => p.Split('=')).Where(p => p[0] == key).Select(p => Uri.UnescapeDataString(p[1])).SingleOrDefault() ?? "<none>";

    /// <summary>模拟服务：原文按段落给出，检测为 zh；target=zh 且有 fallback 时改译为 fallback。</summary>
    private static Task<HttpResponseMessage> ChineseScreenshot(RecordedRequest request, CancellationToken _)
    {
        var target = Query("target", request);
        var fallback = Query("fallback_target", request);
        var actual = target == "zh" && fallback != "<none>" ? fallback : target;
        var route = actual == "zh" ? "[]" : $"[\"opus-mt-zh-{actual}\"]";
        return Task.FromResult(FakeEngineHandler.Json(HttpStatusCode.OK, $$$"""
            {"lines":[{"text":"{{{SecretText}}}","box":[0,0,1,1],"score":0.9}],
             "paragraphs":[{"text":"{{{SecretText}}}","box":[0,0,1,1]}],"text":"{{{SecretText}}}","image":{"width":320,"height":80},
             "translation":{"results":[{"text":"Article 7","source":"zh","detected":true,"target":"{{{actual}}}","route":{{{route}}},"elapsed_ms":5}]},
             "elapsed_ms":{"ocr":100,"translate":5,"total":106}}
            """));
    }

    [Fact]
    public async Task Primary_IsTarget_SecondaryIsFallback_SourceAuto()
    {
        _handler.OcrTranslate = ChineseScreenshot;
        using var service = CreateService();

        var outcome = await service.TranslateImageAsync(TestPng.Small);

        var request = Assert.Single(_handler.OcrRequests);
        Assert.Equal("auto", Query("source", request));
        Assert.Equal("zh", Query("target", request));
        Assert.Equal("en", Query("fallback_target", request));
        Assert.Equal("zh", outcome.Target);
        Assert.Equal("en", outcome.FallbackTarget);
        Assert.True(outcome.Retargeted);
        Assert.Equal("Article 7", outcome.Response.Translation!.Results[0].Text);
        Assert.Equal(TestPng.Small.Length, outcome.ByteCount);
        Assert.Equal((320, 80), (outcome.ImageWidth, outcome.ImageHeight));
    }

    [Fact]
    public async Task EnglishPrimary_NotRetargeted()
    {
        _targets = ("en", "zh");
        _handler.OcrTranslate = ChineseScreenshot;
        using var service = CreateService();

        var outcome = await service.TranslateImageAsync(TestPng.Small);

        Assert.False(outcome.Retargeted);
        Assert.Equal("zh", Query("fallback_target", Assert.Single(_handler.OcrRequests)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zh")]
    [InlineData("zh-Hans")]
    public async Task SecondaryMissingOrSameAsPrimary_NoFallback(string secondary)
    {
        _targets = ("zh", secondary);
        _handler.OcrTranslate = ChineseScreenshot;
        using var service = CreateService();

        var outcome = await service.TranslateImageAsync(TestPng.Small);

        Assert.Null(outcome.FallbackTarget);
        Assert.False(outcome.Retargeted);
        Assert.Equal("<none>", Query("fallback_target", Assert.Single(_handler.OcrRequests)));
    }

    [Fact]
    public async Task ReadsTargetsOnEveryCall()
    {
        _handler.OcrTranslate = ChineseScreenshot;
        using var service = CreateService();

        await service.TranslateImageAsync(TestPng.Small);
        _targets = ("ja", "en");
        await service.TranslateImageAsync(TestPng.Small);

        Assert.Equal(["zh", "ja"], _handler.OcrRequests.Select(r => Query("target", r)));
    }

    [Fact]
    public async Task NewCall_CancelsPrevious()
    {
        var hang = new HangingTranslate();
        _handler.OcrTranslate = hang.HandleRecorded;
        using var service = CreateService();

        var first = service.TranslateImageAsync(TestPng.Small);
        await hang.Started;
        _handler.OcrTranslate = ChineseScreenshot;
        var second = await service.TranslateImageAsync(TestPng.Small);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("Article 7", second.Response.Translation!.Results[0].Text);
    }

    [Fact]
    public async Task CancelCurrent_CancelsInFlight()
    {
        var hang = new HangingTranslate();
        _handler.OcrTranslate = hang.HandleRecorded;
        using var service = CreateService();

        var task = service.TranslateImageAsync(TestPng.Small);
        await hang.Started;
        service.CancelCurrent();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.DoesNotContain(_logger.Lines, l => l.Contains("失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disposed_Throws()
    {
        var service = CreateService();
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TranslateImageAsync(TestPng.Small));
    }

    [Fact]
    public async Task SuccessLog_HasSizeBytesAndTimings_ButNoText()
    {
        _handler.OcrTranslate = ChineseScreenshot;
        using var service = CreateService();

        await service.TranslateImageAsync(TestPng.Small);

        var line = Assert.Single(_logger.Lines);
        Assert.Contains("size=320x80", line, StringComparison.Ordinal);
        Assert.Contains($"bytes={TestPng.Small.Length}", line, StringComparison.Ordinal);
        Assert.Contains("paragraphs=1", line, StringComparison.Ordinal);
        Assert.Contains("server=106ms", line, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretText, line, StringComparison.Ordinal);
        Assert.DoesNotContain("Article", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureLog_HasCodeAndStatus_ButNotServerMessage()
    {
        var body = """{"error":{"code":"ocr_unavailable","message":"SECRET","details":{"missing_models":["ppocr-det"]}}}"""
            .Replace("SECRET", SecretText, StringComparison.Ordinal);
        _handler.OcrTranslate = (_, _) => Task.FromResult(FakeEngineHandler.Json(503, body));
        using var service = CreateService();

        var ex = await Assert.ThrowsAsync<EngineException>(() => service.TranslateImageAsync(TestPng.Small));

        Assert.Equal(EngineErrorKind.OcrUnavailable, ex.Kind);
        var line = Assert.Single(_logger.Lines);
        Assert.Contains("code=ocr_unavailable", line, StringComparison.Ordinal);
        Assert.Contains("status=503", line, StringComparison.Ordinal);
        Assert.Contains("kind=OcrUnavailable", line, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretText, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrecheckFailure_IsLoggedAsPrecheck()
    {
        using var service = CreateService();

        await Assert.ThrowsAsync<EngineException>(() => service.TranslateImageAsync(TestPng.Header(5000, 5000)));

        Assert.Contains("precheck=True", Assert.Single(_logger.Lines), StringComparison.Ordinal);
        Assert.Empty(_handler.Requests);
    }

    private sealed class RecordingLogger : IAppLogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            lock (_lines)
            {
                _lines.Add(message + (exception is null ? string.Empty : " | " + exception.Message));
            }
        }
    }
}
