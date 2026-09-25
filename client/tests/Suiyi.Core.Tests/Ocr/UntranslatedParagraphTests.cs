using Suiyi.Core.Flow;
using Suiyi.Core.Ocr;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Ocr;

/// <summary>译文缺失、用原文代替的段落：结果里标出、浮窗小字提示（#58 验收）。</summary>
public sealed class UntranslatedParagraphTests : IDisposable
{
    private readonly PopupViewModel _popup = new();

    public void Dispose() => _popup.Dispose();

    private static string Result(string text) =>
        $$"""{ "text": "{{text}}", "source": "en", "detected": true, "target": "zh", "route": [], "elapsed_ms": 1.0 }""";

    private static PopupOcrResult Map(string json) => OcrResultMapper.Map(OcrDraftContract.ParseResponse(json), "zh");

    [Fact]
    public void AllTranslated_NoMarks()
    {
        var result = Map($$"""{ "paragraphs": [ { "text": "A" }, { "text": "B" } ], "translation": { "results": [ {{Result("甲")}}, {{Result("乙")}} ] } }""");

        Assert.Empty(result.UntranslatedParagraphs);
        Assert.False(result.HasUntranslated);
        Assert.Equal(string.Empty, PopupText.UntranslatedHint(result));
    }

    [Fact]
    public void FewerResults_MarksMissingTail()
    {
        var result = Map($$"""{ "paragraphs": [ { "text": "A" }, { "text": "B" }, { "text": "C" } ], "translation": { "results": [ {{Result("甲")}} ] } }""");

        Assert.Equal([1, 2], result.UntranslatedParagraphs);
        Assert.True(result.IsUntranslated(1));
        Assert.False(result.IsUntranslated(0));
        Assert.Equal(["甲", "B", "C"], result.TranslationParagraphs);
        Assert.Equal("第 2、3 段未能翻译，显示为原文", PopupText.UntranslatedHint(result));
    }

    [Fact]
    public void EmptyOrBlankTranslationText_IsMarked()
    {
        var result = Map($$"""{ "paragraphs": [ { "text": "A" }, { "text": "B" } ], "translation": { "results": [ {{Result("   ")}}, {{Result("乙")}} ] } }""");

        Assert.Equal([0], result.UntranslatedParagraphs);
        Assert.Equal("第 1 段未能翻译，显示为原文", PopupText.UntranslatedHint(result));
    }

    [Fact]
    public void BlankSourceParagraphsDropped_IndicesFollowDisplayedParagraphs()
    {
        var result = Map($$"""{ "paragraphs": [ { "text": " " }, { "text": "B" }, { "text": "C" } ], "translation": { "results": [ {{Result("")}}, {{Result("乙")}} ] } }""");

        Assert.Equal(["B", "C"], result.SourceParagraphs);
        Assert.Equal([1], result.UntranslatedParagraphs); // C 没有对应结果
        Assert.Equal("第 2 段未能翻译，显示为原文", PopupText.UntranslatedHint(result));
    }

    [Fact]
    public void NoTranslationAtAll_AllMarked_ShowsAllHint()
    {
        var result = Map("""{ "paragraphs": [ { "text": "A" }, { "text": "B" } ] }""");

        Assert.Equal([0, 1], result.UntranslatedParagraphs);
        Assert.Equal(PopupText.AllUntranslatedHint, PopupText.UntranslatedHint(result));
    }

    [Fact]
    public void TranslationSameAsSource_IsNotMarked()
    {
        // 服务端原样返回（原文已是目标语种）不是「缺失」。
        var result = Map($$"""{ "paragraphs": [ { "text": "你好" } ], "translation": { "results": [ {{Result("你好")}} ] } }""");

        Assert.Empty(result.UntranslatedParagraphs);
    }

    [Fact]
    public void Empty_HasNoMarks()
    {
        Assert.Empty(PopupOcrResult.Empty("zh").UntranslatedParagraphs);
        Assert.Equal(string.Empty, PopupText.UntranslatedHint(PopupOcrResult.Empty("zh")));
    }

    [Fact]
    public void Hint_IgnoresOutOfRangeAndDuplicateIndices()
    {
        var result = new PopupOcrResult(["A", "B", "C"], ["甲", "B", "丙"], "en", "zh") { UntranslatedParagraphs = [1, 1, 7, -1] };

        Assert.Equal("第 2 段未能翻译，显示为原文", PopupText.UntranslatedHint(result));
    }

    [Fact]
    public void ViewModel_ShowsHintOnlyForOcrResultWithUntranslated()
    {
        var partial = new PopupOcrResult(["A", "B"], ["甲", "B"], "en", "zh") { UntranslatedParagraphs = [1] };

        _popup.ShowOcrResult(partial);
        Assert.True(_popup.HasUntranslatedHint);
        Assert.Equal("第 2 段未能翻译，显示为原文", _popup.UntranslatedHint);
        Assert.Equal("甲\n\nB", _popup.Translation); // 复制内容不带提示

        _popup.ShowOcrResult(new PopupOcrResult(["A"], ["甲"], "en", "zh"));
        Assert.False(_popup.HasUntranslatedHint);

        _popup.ShowOcrResult(partial);
        _popup.ShowError(new PopupError(PopupErrorKind.Timeout));
        Assert.False(_popup.HasUntranslatedHint);

        _popup.ShowOcrResult(partial);
        _popup.ShowResult(new PopupResult("Hi", "zh", "en"));
        Assert.Equal(string.Empty, _popup.UntranslatedHint);
    }
}
