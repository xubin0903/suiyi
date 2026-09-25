using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Engine;
using Suiyi.Core.Flow;
using Suiyi.Core.Ocr;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Engine;

/// <summary><see cref="EngineClient"/> 的 OCR 部分（⚠ 按 #53 草案；定稿后同步断言）。</summary>
public sealed class EngineClientOcrTests : IDisposable
{
    private const string OcrLoadedHealth = """
        {"status":"ok","version":"0.0.2","models_dir":"/m","uptime_s":1.0,"ocr_loaded":true,
         "loaded_models":["opus-mt-en-jap","opus-mt-en-zh","opus-mt-ja-en","opus-mt-tc-big-zh-ja","opus-mt-zh-en"]}
        """;

    private const string SampleResponse = """
        {
          "lines":[{"text":"今天天气很好。","box":[[0,0],[100,0],[100,20],[0,20]],"score":0.98},
                   {"text":"适合出门散步。","box":[[0,24],[100,24],[100,44],[0,44]],"score":0.97}],
          "paragraphs":[{"text":"今天天气很好。适合出门散步。","box":[0,0,100,44]}],
          "text":"今天天气很好。适合出门散步。",
          "image":{"width":320,"height":80},
          "translation":{"results":[{"text":"The weather is nice today. Good for a walk.","source":"zh","detected":true,"target":"en","route":["opus-mt-zh-en"],"elapsed_ms":80}]},
          "elapsed_ms":{"ocr":210.5,"translate":80,"total":295.5}
        }
        """;

    private readonly FakeEngineHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly EngineClient _client;

    public EngineClientOcrTests()
    {
        _client = new EngineClient(EngineClient.DefaultPort, _handler, _time);
    }

    public void Dispose() => _client.Dispose();

    private static Task<HttpResponseMessage> Respond(int status, string json) =>
        Task.FromResult(FakeEngineHandler.Json(status, json));

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty));

    // ---- 请求格式 ----

    [Fact]
    public async Task OcrTranslateAsync_PostsRawPngWithImagePngContentTypeAndQuery()
    {
        _handler.OcrTranslate = (_, _) => Respond(200, SampleResponse);
        var png = TestPng.Header(320, 80, totalLength: 200);

        await _client.OcrTranslateAsync(png, "auto", "zh", "en");

        var request = Assert.Single(_handler.OcrRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/ocr_translate", request.Path);
        Assert.Equal("image/png", request.ContentType);
        Assert.Null(request.CharSet);
        Assert.Equal(png, request.BodyBytes);
        Assert.Equal(
            new Dictionary<string, string> { ["source"] = "auto", ["target"] = "zh", ["fallback_target"] = "en" },
            ParseQuery(request.Query));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("zh")]
    [InlineData("ZH")]
    [InlineData("zh-CN")]
    public async Task OcrTranslateAsync_FallbackMissingOrSameAsTarget_IsOmitted(string? fallback)
    {
        await _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", fallback);

        var query = ParseQuery(Assert.Single(_handler.OcrRequests).Query);
        Assert.False(query.ContainsKey("fallback_target"));
        Assert.Equal("zh", query["target"]);
    }

    [Fact]
    public async Task OcrTranslateAsync_ExplicitSource_IsSent()
    {
        await _client.OcrTranslateAsync(TestPng.Small, "ja", "zh", "en");

        Assert.Equal("ja", ParseQuery(Assert.Single(_handler.OcrRequests).Query)["source"]);
    }

    [Fact]
    public void BuildOcrTranslatePath_EscapesValues()
    {
        Assert.Equal("ocr_translate?source=auto&target=zh&fallback_target=en", EngineClient.BuildOcrTranslatePath("auto", "zh", "en"));
        Assert.Equal("ocr_translate?source=a%26b&target=zh", EngineClient.BuildOcrTranslatePath("a&b", "zh", null));
    }

    [Fact]
    public async Task OcrTranslateAsync_WarmsCachesOnceThenPosts()
    {
        await _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en");
        await _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en");

        Assert.Equal(["/languages", "/health", "/ocr_translate", "/ocr_translate"], _handler.Requests.Select(r => r.Path));
    }

    [Theory]
    [InlineData("", "source")]
    [InlineData("auto", "target")]
    public async Task OcrTranslateAsync_BlankArguments_Throw(string source, string blank)
    {
        var target = blank == "target" ? " " : "zh";
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _client.OcrTranslateAsync(TestPng.Small, source, target, null));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task OcrTranslateAsync_EmptyPng_ThrowsWithoutRequest()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _client.OcrTranslateAsync(ReadOnlyMemory<byte>.Empty, "auto", "zh", null));
        Assert.Empty(_handler.Requests);
    }

    // ---- 成功解析 ----

    [Fact]
    public async Task OcrTranslateAsync_Success_ParsesDraftFields()
    {
        _handler.OcrTranslate = (_, _) => Respond(200, SampleResponse);

        var response = await _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en");

        Assert.Equal(2, response.Lines.Count);
        Assert.Equal(0.98, response.Lines[0].Score);
        Assert.Equal("今天天气很好。适合出门散步。", Assert.Single(response.Paragraphs).Text);
        Assert.Equal("今天天气很好。适合出门散步。", response.Text);
        Assert.Equal((320, 80), (response.Image!.Width, response.Image.Height));
        var result = Assert.Single(response.Translation!.Results);
        Assert.Equal(("zh", true, "en"), (result.Source, result.Detected, result.Target));
        Assert.Equal(295.5, response.ElapsedMs!.Total);
        Assert.Equal(210.5, response.ElapsedMs.Ocr);
    }

    [Fact]
    public async Task OcrTranslateAsync_EmptyRecognition_IsNormalResult()
    {
        _handler.OcrTranslate = (_, _) => Respond(200, """
            {"lines":[],"paragraphs":[],"text":"","image":{"width":50,"height":20},"translation":{"results":[]},"elapsed_ms":{"ocr":30,"translate":0,"total":31}}
            """);

        var response = await _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en");

        Assert.Empty(response.Paragraphs);
        Assert.Equal(string.Empty, response.Text);
        Assert.Empty(response.Translation!.Results);
        Assert.True(OcrResultMapper.Map(response, "zh").IsEmpty);
    }

    [Fact]
    public async Task OcrTranslateAsync_IgnoresUnknownFields()
    {
        _handler.OcrTranslate = (_, _) => Respond(200, """
            {"lines":[{"text":"Hi","box":{"x":1},"score":0.5,"angle":0}],"paragraphs":[{"text":"Hi","box":null,"lang":"en"}],
             "text":"Hi","image":{"width":1,"height":1,"dpi":96},"model":{"det":"x"},
             "translation":{"results":[{"text":"嗨","source":"en","detected":true,"target":"zh","route":["m"],"elapsed_ms":1,"confidence":0.9}],"batch":true},
             "elapsed_ms":{"ocr":1,"translate":1,"total":2,"decode":0.1}}
            """);

        var response = await _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", null);

        Assert.Equal("嗨", Assert.Single(response.Translation!.Results).Text);
        Assert.Equal("Hi", Assert.Single(response.Paragraphs).Text);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task OcrTranslateAsync_OkWithBadBody_IsUnknown(string body)
    {
        _handler.OcrTranslate = (_, _) => Respond(200, body);

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", null));

        Assert.Equal(EngineErrorKind.Unknown, ex.Kind);
        Assert.Equal(200, ex.StatusCode);
    }

    // ---- 错误码映射 ----

    [Fact]
    public async Task OcrTranslateAsync_ImageTooLarge_CarriesLimitActualAndDetails()
    {
        _handler.OcrTranslate = (_, _) => Respond(413, """
            {"error":{"code":"image_too_large","message":"图片过大","details":{"limit":8388608,"actual":9000000}}}
            """);

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", null));

        Assert.Equal(EngineErrorKind.ImageTooLarge, ex.Kind);
        Assert.Equal(413, ex.StatusCode);
        Assert.Equal("image_too_large", ex.ErrorCode);
        Assert.Equal(8388608, ex.Limit);
        Assert.Equal(9000000, ex.Length);
        Assert.False(ex.IsClientPrecheck);
        Assert.Equal(9000000, ex.Details.GetProperty("actual").GetInt32());
        Assert.StartsWith("ocr_translate 返回 HTTP 413", ex.Message, StringComparison.Ordinal);
        Assert.Equal("选区过大，请缩小后重试", ex.UserMessage);
    }

    [Fact]
    public async Task OcrTranslateAsync_OcrUnavailable_CarriesMissingModels()
    {
        _handler.OcrTranslate = (_, _) => Respond(503, """
            {"error":{"code":"ocr_unavailable","message":"缺模型","details":{"missing_models":["ppocr-det","ppocr-rec-zh"]}}}
            """);

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", null));

        Assert.Equal(EngineErrorKind.OcrUnavailable, ex.Kind);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(["ppocr-det", "ppocr-rec-zh"], ex.MissingModels);
        Assert.Equal("OCR 模型未安装：ppocr-det、ppocr-rec-zh", ex.UserMessage);
    }

    [Theory]
    [InlineData(415, """{"error":{"code":"unsupported_media_type","message":"","details":{}}}""", EngineErrorKind.UnsupportedMediaType, "截图格式不受支持")]
    [InlineData(422, """{"error":{"code":"invalid_image","message":"","details":{}}}""", EngineErrorKind.InvalidImage, "截图无法解码")]
    [InlineData(503, """{"error":{"code":"ocr_unavailable","message":"","details":{}}}""", EngineErrorKind.OcrUnavailable, "OCR 模型未安装")]
    [InlineData(422, """{"error":{"code":"unsupported_pair","message":"","details":{"source":"ko","target":"zh","missing_models":[]}}}""", EngineErrorKind.UnsupportedPair, "不支持该语向：ko→zh")]
    [InlineData(413, """{"error":{"code":"text_too_long","message":"","details":{"limit":20000,"length":20001}}}""", EngineErrorKind.TextTooLong, "文本过长：20001 字，上限 20000 字")]
    [InlineData(422, """{"error":{"code":"detect_failed","message":"","details":{}}}""", EngineErrorKind.DetectFailed, "无法识别原文语种，请手动指定")]
    [InlineData(422, """{"error":{"code":"invalid_request","message":"","details":{"field":"target"}}}""", EngineErrorKind.InvalidRequest, "翻译请求无效")]
    [InlineData(500, """{"error":{"code":"internal_error","message":"","details":{}}}""", EngineErrorKind.Internal, "翻译服务内部错误")]
    [InlineData(503, """{"error":{"code":"brand_new_code","message":"","details":{}}}""", EngineErrorKind.Internal, "翻译服务内部错误")]
    [InlineData(422, """{"error":{"code":"brand_new_code","message":"","details":{}}}""", EngineErrorKind.Unknown, "翻译服务返回了无法识别的响应")]
    [InlineData(404, """{"detail":"Not Found"}""", EngineErrorKind.Unknown, "翻译服务返回了无法识别的响应")]
    [InlineData(413, "<html>Request Entity Too Large</html>", EngineErrorKind.Unknown, "翻译服务返回了无法识别的响应")]
    [InlineData(502, "", EngineErrorKind.Internal, "翻译服务内部错误")]
    [InlineData(422, """{"error":"oops"}""", EngineErrorKind.Unknown, "翻译服务返回了无法识别的响应")]
    public async Task OcrTranslateAsync_ErrorResponses_MapToKinds(int status, string body, EngineErrorKind kind, string userMessage)
    {
        _handler.OcrTranslate = (_, _) => Respond(status, body);

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", null));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(userMessage, ex.UserMessage);
        Assert.DoesNotContain("?", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(413, """{"error":{"code":"image_too_large","message":"","details":{"limit":16777216,"actual":20000000}}}""", PopupErrorKind.ImageTooLarge)]
    [InlineData(415, """{"error":{"code":"unsupported_media_type","message":"","details":{}}}""", PopupErrorKind.InvalidImage)]
    [InlineData(422, """{"error":{"code":"invalid_image","message":"","details":{}}}""", PopupErrorKind.InvalidImage)]
    [InlineData(503, """{"error":{"code":"ocr_unavailable","message":"","details":{"missing_models":["ppocr-det"]}}}""", PopupErrorKind.OcrUnavailable)]
    [InlineData(422, """{"error":{"code":"unsupported_pair","message":"","details":{"missing_models":["opus-mt-zh-en"]}}}""", PopupErrorKind.MissingModels)]
    [InlineData(422, """{"error":{"code":"detect_failed","message":"","details":{}}}""", PopupErrorKind.DetectFailed)]
    public async Task ErrorResponses_ThroughPopupErrorMapper_MatchOcrResultMapper(int status, string body, PopupErrorKind expected)
    {
        _handler.OcrTranslate = (_, _) => Respond(status, body);

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", null));
        var viaException = PopupErrorMapper.Map(ex);
        var viaEnvelope = OcrResultMapper.MapError(OcrDraftContract.ParseError(body));

        Assert.Equal(expected, viaException.Kind);
        Assert.Equal(viaEnvelope.Kind, viaException.Kind);
        Assert.Equal(viaEnvelope.Message, viaException.Message);
        Assert.Equal(viaEnvelope.MissingModels, viaException.MissingModels);
        Assert.Equal(viaEnvelope.Limit, viaException.Limit);
    }

    // ---- 超时 ----

    [Theory]
    [InlineData(FakeEngineHandler.AllLoadedHealth, 30000)] // 旧引擎没有 ocr_loaded
    [InlineData(OcrLoadedHealth, 15000)]
    [InlineData("""{"status":"ok","version":"0.0.2","models_dir":"/m","uptime_s":1,"ocr_loaded":false,"loaded_models":["opus-mt-zh-en","opus-mt-en-zh","opus-mt-ja-en","opus-mt-en-jap","opus-mt-tc-big-zh-ja"]}""", 30000)]
    [InlineData("""{"status":"ok","version":"0.0.2","models_dir":"/m","uptime_s":1,"ocr_loaded":true,"loaded_models":[]}""", 30000)]
    public async Task OcrTranslateAsync_TimesOutExactlyAtPolicyValue(string health, int expectedMs)
    {
        _handler.Health = health;
        var hang = new HangingTranslate();
        _handler.OcrTranslate = hang.HandleRecorded;

        var task = _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en");
        await hang.Started;

        _time.Advance(TimeSpan.FromMilliseconds(expectedMs - 1));
        await Task.Yield();
        Assert.False(task.IsCompleted, $"在 {expectedMs - 1} ms 时不应超时");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        var ex = await Assert.ThrowsAsync<EngineException>(() => task);
        Assert.Equal(EngineErrorKind.Timeout, ex.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), ex.Timeout);
        Assert.StartsWith("ocr_translate 超过", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OcrTranslateAsync_Success_MarksOcrAndRouteModelsLoaded()
    {
        _handler.Health = """{"status":"ok","version":"0.0.2","models_dir":"/m","uptime_s":1,"ocr_loaded":false,"loaded_models":[]}""";
        _handler.Languages = """{"languages":["en","zh"],"pairs":[{"src":"zh","tgt":"en","route":"direct","models":["opus-mt-zh-en"]},{"src":"en","tgt":"zh","route":"direct","models":["opus-mt-en-zh"]}]}""";
        _handler.OcrTranslate = (_, _) => Respond(200, SampleResponse);

        await _client.OcrTranslateAsync(TestPng.Small, "auto", "en", "zh");
        Assert.Equal(TimeSpan.FromMilliseconds(30000), _client.GetOcrTranslateTimeout("auto", "en", "zh"));
        Assert.True(_client.KnownOcrLoaded);
        Assert.Contains("opus-mt-zh-en", _client.KnownLoadedModels!);

        // en→zh 的模型还没用过，仍按冷加载算；只看 zh→en 且无次目标时已全部加载。
        Assert.Equal(TimeSpan.FromMilliseconds(15000), _client.GetOcrTranslateTimeout("zh", "en", null));
    }

    [Fact]
    public async Task KnownOcrLoaded_FollowsHealthAndInvalidate()
    {
        Assert.Null(_client.KnownOcrLoaded);

        await _client.GetHealthAsync();
        Assert.Null(_client.KnownOcrLoaded); // 旧引擎没有字段

        _handler.Health = OcrLoadedHealth;
        var health = await _client.GetHealthAsync();
        Assert.True(health.OcrLoaded);
        Assert.True(_client.KnownOcrLoaded);

        _client.Invalidate();
        Assert.Null(_client.KnownOcrLoaded);
    }

    // ---- 取消与连接失败 ----

    [Fact]
    public async Task OcrTranslateAsync_CallerCancels_ThrowsOperationCanceled()
    {
        var hang = new HangingTranslate();
        _handler.OcrTranslate = hang.HandleRecorded;
        using var cts = new CancellationTokenSource();

        var task = _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en", cts.Token);
        await hang.Started;
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task OcrTranslateAsync_AlreadyCanceled_DoesNotPost()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en", cts.Token));

        Assert.Empty(_handler.OcrRequests);
    }

    [Fact]
    public async Task OcrTranslateAsync_ConnectionRefused_IsUnavailable()
    {
        using var client = new EngineClient(EngineClient.DefaultPort, new ThrowingHandler(() => throw new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Connection refused (127.0.0.1:18780)",
            new SocketException((int)SocketError.ConnectionRefused))), _time);

        var ex = await Assert.ThrowsAsync<EngineException>(() => client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en"));

        Assert.Equal(EngineErrorKind.Unavailable, ex.Kind);
        Assert.Null(ex.StatusCode);
        Assert.Equal(PopupErrorKind.ServiceUnavailable, PopupErrorMapper.Map(ex).Kind);
    }

    [Fact]
    public async Task OcrTranslateAsync_ConnectionResetWhileUploading_IsUnavailable()
    {
        _handler.OcrTranslate = (_, _) => throw new IOException("connection reset");

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en"));

        Assert.Equal(EngineErrorKind.Unavailable, ex.Kind);
    }

    // ---- 客户端预检 ----

    [Fact]
    public async Task OcrTranslateAsync_OverByteLimit_RejectedWithoutAnyRequest()
    {
        var png = TestPng.Header(100, 100, OcrDraftContract.MaxImageBytes + 1);

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrTranslateAsync(png, "auto", "zh", "en"));

        Assert.Equal(EngineErrorKind.ImageTooLarge, ex.Kind);
        Assert.True(ex.IsClientPrecheck);
        Assert.Null(ex.StatusCode);
        Assert.Equal("image_too_large", ex.ErrorCode);
        Assert.Equal(OcrDraftContract.MaxImageBytes, ex.Limit);
        Assert.Equal(OcrDraftContract.MaxImageBytes + 1, ex.Length);
        Assert.Empty(_handler.Requests);
        var popup = PopupErrorMapper.Map(ex);
        Assert.Equal(PopupErrorKind.ImageTooLarge, popup.Kind);
        Assert.False(popup.CanRetry);
    }

    [Fact]
    public async Task OcrTranslateAsync_OverPixelLimit_RejectedWithoutAnyRequest()
    {
        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrTranslateAsync(TestPng.Header(5000, 4000), "auto", "zh", "en"));

        Assert.Equal(EngineErrorKind.ImageTooLarge, ex.Kind);
        Assert.True(ex.IsClientPrecheck);
        Assert.Equal(20_000_000, ex.Length);
        Assert.Equal(16_777_216, ex.Details.GetProperty("limit").GetInt64());
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task OcrAsync_OverByteLimit_RejectedWithoutRequest()
    {
        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrAsync(new byte[OcrDraftContract.MaxImageBytes + 1]));

        Assert.True(ex.IsClientPrecheck);
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData(4096, 4096)]
    [InlineData(8000, 1000)] // 细长图总像素未超，交给服务端判断
    [InlineData(1, 1)]
    public void PrecheckImage_WithinLimits_Passes(int width, int height)
    {
        Assert.Null(EngineClient.PrecheckImage(TestPng.Header(width, height)));
    }

    [Fact]
    public void PrecheckImage_ExactlyByteLimit_Passes()
    {
        Assert.Null(EngineClient.PrecheckImage(TestPng.Header(10, 10, OcrDraftContract.MaxImageBytes)));
    }

    [Fact]
    public async Task OcrTranslateAsync_NotPng_IsSentAndServerDecides()
    {
        _handler.OcrTranslate = (_, _) => Respond(415, """{"error":{"code":"unsupported_media_type","message":"","details":{}}}""");

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrTranslateAsync("GIF89a..."u8.ToArray(), "auto", "zh", null));

        Assert.Equal(EngineErrorKind.UnsupportedMediaType, ex.Kind);
        Assert.Single(_handler.OcrRequests);
    }

    [Fact]
    public void TryReadPngSize_ReadsIhdr()
    {
        Assert.True(EngineClient.TryReadPngSize(TestPng.Header(1920, 1080), out var w, out var h));
        Assert.Equal((1920, 1080), (w, h));
    }

    [Fact]
    public void TryReadPngSize_RejectsNonPngAndTruncated()
    {
        var png = TestPng.Header(10, 10);
        Assert.False(EngineClient.TryReadPngSize(png.AsSpan(0, 23), out _, out _));
        Assert.False(EngineClient.TryReadPngSize("not a png at all, just text"u8, out _, out _));
        var badChunk = (byte[])png.Clone();
        badChunk[12] = (byte)'X';
        Assert.False(EngineClient.TryReadPngSize(badChunk, out _, out _));
        Assert.False(EngineClient.TryReadPngSize(TestPng.Header(0, 10), out _, out _));
    }

    // ---- /ocr ----

    [Fact]
    public async Task OcrAsync_PostsPngWithLangAndParsesNumericElapsed()
    {
        _handler.Health = OcrLoadedHealth;
        _handler.Ocr = (_, _) => Respond(200, """
            {"lines":[{"text":"Hello","box":[0,0,1,1],"score":0.9}],"paragraphs":[{"text":"Hello","box":[0,0,1,1]}],"text":"Hello","image":{"width":64,"height":16},"elapsed_ms":42.5}
            """);

        var response = await _client.OcrAsync(TestPng.Small, "en");

        var request = Assert.Single(_handler.OcrRequests);
        Assert.Equal("/ocr", request.Path);
        Assert.Equal("image/png", request.ContentType);
        Assert.Equal("lang=en", request.Query);
        Assert.Equal("Hello", response.Text);
        Assert.Equal(42.5, response.ElapsedMs);
        Assert.True(_client.KnownOcrLoaded);
    }

    [Fact]
    public async Task OcrAsync_DefaultLangIsAuto_AndErrorsMap()
    {
        _handler.Ocr = (_, _) => Respond(422, """{"error":{"code":"invalid_image","message":"","details":{}}}""");

        var ex = await Assert.ThrowsAsync<EngineException>(() => _client.OcrAsync(TestPng.Small));

        Assert.Equal("lang=auto", Assert.Single(_handler.OcrRequests).Query);
        Assert.Equal(EngineErrorKind.InvalidImage, ex.Kind);
        Assert.StartsWith("ocr 返回 HTTP 422", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OcrAsync_TimeoutIsColdWhenOcrNotKnownLoaded()
    {
        var hang = new HangingTranslate();
        _handler.Ocr = hang.HandleRecorded;

        var task = _client.OcrAsync(TestPng.Small);
        await hang.Started;
        _time.Advance(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrColdMs));

        var ex = await Assert.ThrowsAsync<EngineException>(() => task);
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.OcrColdMs), ex.Timeout);
    }

    [Fact]
    public void UserMessage_OcrKindsAreChinese()
    {
        foreach (var kind in new[] { EngineErrorKind.ImageTooLarge, EngineErrorKind.UnsupportedMediaType, EngineErrorKind.InvalidImage, EngineErrorKind.OcrUnavailable })
        {
            Assert.Contains(new EngineException(kind, "x").UserMessage, c => c >= '\u4e00' && c <= '\u9fff');
        }
    }

    [Fact]
    public void Details_SurvivesAfterMapping()
    {
        var ex = EngineClient.MapError(413, """{"error":{"code":"image_too_large","message":"","details":{"limit":1,"actual":2}}}""", "ocr_translate");

        Assert.Equal(JsonValueKind.Object, ex.Details.ValueKind);
        Assert.Equal(2, ex.Details.GetProperty("actual").GetInt32());
    }

    private sealed class ThrowingHandler(Func<HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send());
    }
}
