using System.Text.Json;
using Suiyi.Core.Flow;
using Suiyi.Core.Ocr;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Ocr;

/// <summary>按 #53 草案的 <c>/ocr_translate</c> 响应与错误 → 浮窗内容。草案定稿后同步这里。</summary>
public sealed class OcrResultMapperTests
{
    private static PopupOcrResult Map(string json, string target = "en") =>
        OcrResultMapper.Map(OcrDraftContract.ParseResponse(json), target);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Result(string text, string source, string target, bool detected = true) =>
        $$"""{ "text": "{{text}}", "source": "{{source}}", "detected": {{(detected ? "true" : "false")}}, "target": "{{target}}", "route": [], "elapsed_ms": 1.0 }""";

    // ---- 成功 ----

    [Fact]
    public void Normal_ParagraphsPairedWithTranslations()
    {
        var result = Map($$"""
            {
              "lines": [ { "text": "你好", "box": [[0,0],[1,0],[1,1],[0,1]], "score": 0.99 } ],
              "paragraphs": [ { "text": "你好。", "box": [0, 0, 10, 10] }, { "text": "再见。", "box": [0, 20, 10, 10] } ],
              "text": "你好。\n再见。",
              "image": { "width": 100, "height": 50 },
              "translation": { "results": [ {{Result("Hello.", "zh", "en")}}, {{Result("Goodbye.", "zh", "en")}} ] },
              "elapsed_ms": { "ocr": 500.5, "translate": 200.0, "total": 712.3 }
            }
            """);

        Assert.Equal(["你好。", "再见。"], result.SourceParagraphs);
        Assert.Equal(["Hello.", "Goodbye."], result.TranslationParagraphs);
        Assert.Equal("zh", result.Source);
        Assert.Equal("en", result.Target);
        Assert.True(result.SourceDetected);
        Assert.Equal(TimeSpan.FromMilliseconds(712.3), result.Elapsed);
        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void EmptyRecognition_Returns200Shape_IsEmpty()
    {
        var result = Map("""{ "lines": [], "paragraphs": [], "text": "", "image": { "width": 10, "height": 10 }, "translation": { "results": [] }, "elapsed_ms": { "ocr": 50, "translate": 0, "total": 51 } }""", "ja");

        Assert.True(result.IsEmpty);
        Assert.Equal("ja", result.Target);
        Assert.Equal(TimeSpan.FromMilliseconds(51), result.Elapsed);
    }

    [Fact]
    public void MinimalBody_Empty()
    {
        var result = Map("{}");

        Assert.True(result.IsEmpty);
        Assert.Null(result.Elapsed);
    }

    [Fact]
    public void BlankParagraphs_Dropped_IndicesStillAligned()
    {
        var result = Map($$"""
            {
              "paragraphs": [ { "text": "A" }, { "text": "  " }, { "text": "C" } ],
              "translation": { "results": [ {{Result("甲", "en", "zh")}}, {{Result("", "en", "zh")}}, {{Result("丙", "en", "zh")}} ] }
            }
            """, "zh");

        Assert.Equal(["A", "C"], result.SourceParagraphs);
        Assert.Equal(["甲", "丙"], result.TranslationParagraphs);
    }

    [Fact]
    public void FewerTranslations_MissingUseSource()
    {
        var result = Map($$"""
            { "paragraphs": [ { "text": "One." }, { "text": "Two." } ], "translation": { "results": [ {{Result("一。", "en", "zh")}} ] } }
            """, "zh");

        Assert.Equal(["一。", "Two."], result.TranslationParagraphs);
        Assert.Equal("en", result.Source);
    }

    [Fact]
    public void NoTranslationObject_UsesRequestedTarget()
    {
        var result = Map("""{ "paragraphs": [ { "text": "Hello" } ] }""", "zh");

        Assert.Equal(["Hello"], result.TranslationParagraphs);
        Assert.Null(result.Source);
        Assert.Equal("zh", result.Target);
        Assert.False(result.SourceDetected);
    }

    [Fact]
    public void ParagraphsMissing_FallsBackToTextLines()
    {
        var result = Map("""{ "text": "第一行\n\n第二行", "translation": { "results": [] } }""");

        Assert.Equal(["第一行", "第二行"], result.SourceParagraphs);
    }

    [Fact]
    public void MixedLanguages_LabelUsesMostCommon()
    {
        // 主目标 zh、次目标 en：中文段落改译成英文（fallback_target），其余译成中文。
        var result = Map($$"""
            {
              "paragraphs": [ { "text": "Hello" }, { "text": "你好" }, { "text": "World" } ],
              "translation": { "results": [ {{Result("你好", "en", "zh")}}, {{Result("Hello", "zh", "en")}}, {{Result("世界", "en", "zh")}} ] }
            }
            """, "zh");

        Assert.Equal("en", result.Source);
        Assert.Equal("zh", result.Target);
    }

    [Fact]
    public void Tie_LabelUsesFirst()
    {
        var result = Map($$"""
            { "paragraphs": [ { "text": "こんにちは" }, { "text": "Hi" } ], "translation": { "results": [ {{Result("你好", "ja", "zh")}}, {{Result("嗨", "en", "zh")}} ] } }
            """, "zh");

        Assert.Equal("ja", result.Source);
    }

    [Fact]
    public void ExplicitSource_NotDetected()
    {
        var result = Map($$"""{ "paragraphs": [ { "text": "Hi" } ], "translation": { "results": [ {{Result("嗨", "en", "zh", detected: false)}} ] } }""", "zh");

        Assert.False(result.SourceDetected);
    }

    [Fact]
    public void UnknownFields_Ignored()
    {
        var result = Map($$"""
            { "future": 1, "paragraphs": [ { "text": "Hi", "extra": true, "box": "anything" } ], "translation": { "results": [ {{Result("嗨", "en", "zh")}} ], "more": [] }, "elapsed_ms": { "total": 5, "queue": 1 } }
            """, "zh");

        Assert.Equal(["嗨"], result.TranslationParagraphs);
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => OcrDraftContract.ParseResponse("not json"));
        Assert.ThrowsAny<JsonException>(() => OcrDraftContract.ParseResponse("null"));
    }

    [Fact]
    public void Parse_KeepsLinesAndImage()
    {
        var response = OcrDraftContract.ParseResponse("""{ "lines": [ { "text": "a", "box": [1,2,3,4], "score": 0.5 } ], "image": { "width": 640, "height": 480 }, "elapsed_ms": { "ocr": 1, "translate": 2, "total": 3 } }""");

        Assert.Equal(0.5, Assert.Single(response.Lines).Score);
        Assert.Equal(640, response.Image!.Width);
        Assert.Equal(2, response.ElapsedMs!.Translate);
    }

    [Fact]
    public void Map_Arguments_Validated()
    {
        Assert.Throws<ArgumentNullException>(() => OcrResultMapper.Map(null!, "en"));
        Assert.Throws<ArgumentException>(() => OcrResultMapper.Map(new OcrTranslateResponse(), " "));
    }

    // ---- 错误 ----

    [Fact]
    public void Error_ImageTooLarge()
    {
        var error = OcrResultMapper.MapError(OcrErrorCodes.ImageTooLarge, Json("""{ "limit": 8388608, "actual": 9000000 }"""));

        Assert.Equal(PopupErrorKind.ImageTooLarge, error.Kind);
        Assert.Equal(8388608, error.Limit);
        Assert.Equal(9000000, error.Length);
        Assert.False(error.CanRetry);
    }

    [Theory]
    [InlineData(OcrErrorCodes.UnsupportedMediaType)]
    [InlineData(OcrErrorCodes.InvalidImage)]
    public void Error_BadImage(string code)
    {
        var error = OcrResultMapper.MapError(code);

        Assert.Equal(PopupErrorKind.InvalidImage, error.Kind);
        Assert.True(error.CanRetry);
    }

    [Fact]
    public void Error_OcrUnavailable_WithModels()
    {
        var error = OcrResultMapper.MapError(OcrErrorCodes.OcrUnavailable, Json("""{ "missing_models": ["det", "rec", 3] }"""));

        Assert.Equal(PopupErrorKind.OcrUnavailable, error.Kind);
        Assert.Equal(["det", "rec"], error.MissingModels);
        Assert.Equal("OCR 模型未安装：det、rec", error.Message);
    }

    [Fact]
    public void Error_TranslationSideCodes()
    {
        var pair = OcrResultMapper.MapError(OcrErrorCodes.UnsupportedPair, Json("""{ "source": "ja", "target": "zh", "missing_models": ["opus-mt-ja-en"] }"""));
        Assert.Equal(PopupErrorKind.MissingModels, pair.Kind);
        Assert.Equal(["opus-mt-ja-en"], pair.MissingModels);

        var tooLong = OcrResultMapper.MapError(OcrErrorCodes.TextTooLong, Json("""{ "limit": 10000, "length": 12000, "index": 3 }"""));
        Assert.Equal(PopupErrorKind.TextTooLong, tooLong.Kind);
        Assert.Equal("文本过长：12000 字，上限 10000 字", tooLong.Message);

        Assert.Equal(PopupErrorKind.DetectFailed, OcrResultMapper.MapError(OcrErrorCodes.DetectFailed).Kind);
    }

    [Theory]
    [InlineData("internal_error")]
    [InlineData("something_new")]
    [InlineData("")]
    [InlineData(null)]
    public void Error_Unknown_Other(string? code)
    {
        var error = OcrResultMapper.MapError(code);

        Assert.Equal(PopupErrorKind.Other, error.Kind);
        Assert.Equal("识别或翻译失败，请重试", error.Message);
        Assert.True(error.CanRetry);
    }

    [Fact]
    public void Error_DetailsMissingOrWrongType_Tolerated()
    {
        Assert.Empty(OcrResultMapper.MapError(OcrErrorCodes.OcrUnavailable, Json("[]")).MissingModels);
        Assert.Null(OcrResultMapper.MapError(OcrErrorCodes.ImageTooLarge, Json("""{ "limit": "big" }""")).Limit);
        Assert.Null(OcrResultMapper.MapError(OcrErrorCodes.ImageTooLarge).Limit);
    }

    [Fact]
    public void Error_FromEnvelope()
    {
        var body = OcrDraftContract.ParseError("""{ "error": { "code": "ocr_unavailable", "message": "缺少 OCR 模型", "details": { "missing_models": ["det"] } } }""");

        Assert.NotNull(body);
        Assert.Equal(PopupErrorKind.OcrUnavailable, OcrResultMapper.MapError(body).Kind);
        Assert.Null(OcrDraftContract.ParseError("""{ "detail": "Not Found" }"""));
        Assert.Null(OcrDraftContract.ParseError("<html>"));
        Assert.Equal(PopupErrorKind.Other, OcrResultMapper.MapError((Suiyi.Core.Engine.ErrorBody?)null).Kind);
    }

    [Fact]
    public void ErrorCodes_MatchDraft()
    {
        Assert.Equal("image_too_large", OcrErrorCodes.ImageTooLarge);
        Assert.Equal("unsupported_media_type", OcrErrorCodes.UnsupportedMediaType);
        Assert.Equal("invalid_image", OcrErrorCodes.InvalidImage);
        Assert.Equal("ocr_unavailable", OcrErrorCodes.OcrUnavailable);
    }
}
