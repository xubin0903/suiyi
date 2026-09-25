using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Popup;

public sealed class PopupDemoTests
{
    [Fact]
    public void OneCycle_CoversAllStatesAndErrors()
    {
        var clock = new FakeTimeProvider();
        using var popup = new PopupViewModel(timeProvider: clock);
        var kinds = new List<PopupKind>();
        var errors = new List<PopupErrorKind>();
        var targets = new HashSet<string>();

        for (var step = 0; step < PopupDemo.StepCount; step++)
        {
            PopupDemo.ApplyStep(popup, step);
            clock.Advance(PopupDemo.Interval);
            kinds.Add(popup.Kind);
            if (popup.Error is { } error)
            {
                errors.Add(error.Kind);
            }

            if (popup.Kind == PopupKind.Result)
            {
                targets.Add(popup.LanguageLabel.Split(" → ")[1]);
            }

            Assert.True(popup.IsVisible);
        }

        Assert.Equal(PopupKind.Preparing, kinds[0]);
        Assert.Equal(PopupKind.Loading, kinds[1]);
        Assert.Contains(PopupKind.Result, kinds);
        Assert.Equal(
            [
                PopupErrorKind.ServiceUnavailable, PopupErrorKind.EngineStartTimeout, PopupErrorKind.Timeout, PopupErrorKind.MissingModels, PopupErrorKind.DetectFailed, PopupErrorKind.TextTooLong,
                PopupErrorKind.ImageTooLarge, PopupErrorKind.InvalidImage, PopupErrorKind.OcrUnavailable,
            ],
            errors);
        Assert.Contains(PopupKind.Empty, kinds);
        Assert.Equal(new HashSet<string> { "中文", "English", "日本語" }, targets);
    }

    [Fact]
    public void Interval_IsThreeSeconds_ShorterThanAutoHide()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), PopupDemo.Interval);
        Assert.True(PopupDemo.Interval < TimeSpan.FromSeconds(new PopupOptions().AutoHideSeconds));
    }

    [Fact]
    public void Step_WrapsAndValidates()
    {
        using var popup = new PopupViewModel(timeProvider: new FakeTimeProvider());
        PopupDemo.ApplyStep(popup, PopupDemo.StepCount);
        Assert.Equal(PopupKind.Preparing, popup.Kind);

        PopupDemo.ApplyStep(popup, -1);
        Assert.Equal(PopupKind.Error, popup.Kind);

        Assert.Throws<ArgumentNullException>(() => PopupDemo.ApplyStep(null!, 0));
    }

    [Fact]
    public void OcrSteps_ModeAnchorAndExpansion()
    {
        var clock = new FakeTimeProvider();
        using var popup = new PopupViewModel(timeProvider: clock);
        var sawOcrLoading = false;
        var sawExpanded = false;
        var sawCornerAnchor = false;

        for (var step = 0; step < PopupDemo.StepCount; step++)
        {
            PopupDemo.ApplyStep(popup, step);
            if (popup.Mode != PopupContentMode.Ocr)
            {
                Assert.Null(popup.AnchorRect);
                continue;
            }

            Assert.NotNull(popup.AnchorRect);
            sawOcrLoading |= popup.Kind == PopupKind.Loading && popup.SourcePreview == PopupText.OcrLoadingText;
            sawExpanded |= popup.IsOriginalExpanded && popup.HasOriginal;
            sawCornerAnchor |= popup.AnchorRect == PopupDemo.DemoSelectionNearCorner;
        }

        Assert.True(sawOcrLoading);
        Assert.True(sawExpanded);
        Assert.True(sawCornerAnchor);
    }

    [Fact]
    public void Samples_MapThroughDraftContract()
    {
        var zhEn = PopupDemo.Ocr(PopupDemo.SampleZhEnJson, "en");
        Assert.Equal(2, zhEn.SourceParagraphs.Count);
        Assert.Equal("zh", zhEn.Source);
        Assert.Equal("en", zhEn.Target);
        Assert.Equal(TimeSpan.FromMilliseconds(890.4), zhEn.Elapsed);

        var jaZh = PopupDemo.Ocr(PopupDemo.SampleJaZhJson, "zh");
        Assert.Equal("今天天气晴朗。", jaZh.TranslationText);

        Assert.True(PopupDemo.Ocr(PopupDemo.SampleEmptyJson, "en").IsEmpty);
        Assert.Equal(10, PopupDemo.LongOcrResult().TranslationParagraphs.Count);
    }
}
