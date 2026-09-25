using System.ComponentModel;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Popup;

/// <summary>浮窗的框选翻译（OCR）模式（#57）。</summary>
public sealed class PopupOcrViewModelTests : IDisposable
{
    private static readonly PopupRect Selection = new(100, 100, 640, 240);

    private static readonly PopupOcrResult TwoParagraphs = new(
        ["随译是一款开源免费的本地翻译工具。", "所有识别和翻译都在本机完成。"],
        ["Suiyi is a free, open-source local translator.", "Everything happens on this computer."],
        "zh",
        "en")
    { SourceDetected = true, Elapsed = TimeSpan.FromMilliseconds(890) };

    private readonly FakeTimeProvider _clock = new();
    private readonly PopupViewModel _popup;
    private readonly List<bool> _shown = [];
    private readonly List<string> _copiedTranslation = [];
    private readonly List<string> _copiedOriginal = [];
    private readonly List<string?> _changed = [];

    public PopupOcrViewModelTests()
    {
        _popup = new PopupViewModel(timeProvider: _clock);
        _popup.Shown += (_, e) => _shown.Add(e.Reposition);
        _popup.CopyTranslationRequested += (_, e) => _copiedTranslation.Add(e.Text);
        _popup.CopyOriginalRequested += (_, e) => _copiedOriginal.Add(e.Text);
        ((INotifyPropertyChanged)_popup).PropertyChanged += (_, e) => _changed.Add(e.PropertyName);
    }

    public void Dispose() => _popup.Dispose();

    private void Advance(double ms) => _clock.Advance(TimeSpan.FromMilliseconds(ms));

    // ---- 加载态 ----

    [Fact]
    public void OcrLoading_TextModeAnchorAndDelayedWindow()
    {
        _popup.ShowOcrLoading(Selection);

        Assert.Equal(PopupKind.Loading, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.Equal(Selection, _popup.AnchorRect);
        Assert.Equal("正在识别并翻译…", _popup.SourcePreview);
        Assert.False(_popup.IsVisible);

        Advance(300);
        Assert.True(_popup.IsVisible);
        Assert.True(_popup.ShowLoadingIndicator);
        Assert.Equal([true], _shown);
    }

    [Fact]
    public void OcrLoading_ResultBeforeDelay_NoFlash()
    {
        _popup.ShowOcrLoading(Selection);
        Advance(100);
        _popup.ShowOcrResult(TwoParagraphs);

        Assert.Equal(PopupKind.Result, _popup.Kind);
        Assert.Equal(Selection, _popup.AnchorRect);
        Assert.False(_popup.ShowLoadingIndicator);
        Assert.Equal([true], _shown);
        Advance(1000);
        Assert.False(_popup.ShowLoadingIndicator);
    }

    [Fact]
    public void OcrLoading_WithoutAnchor_CursorPlacement()
    {
        _popup.ShowOcrLoading();

        Assert.Null(_popup.AnchorRect);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
    }

    // ---- 结果 ----

    [Fact]
    public void Result_TranslationMain_OriginalCollapsed()
    {
        _popup.ShowOcrLoading(Selection);
        _popup.ShowOcrResult(TwoParagraphs);

        Assert.Equal("Suiyi is a free, open-source local translator.\n\nEverything happens on this computer.", _popup.Translation);
        Assert.Equal("随译是一款开源免费的本地翻译工具。\n\n所有识别和翻译都在本机完成。", _popup.OriginalText);
        Assert.True(_popup.HasOriginal);
        Assert.False(_popup.IsOriginalExpanded);
        Assert.Equal("中文（自动） → English", _popup.LanguageLabel);
        Assert.Equal("890 ms", _popup.ElapsedText);
        Assert.True(_popup.CanCopy);
        Assert.True(_popup.CanCopyOriginal);
        Assert.False(_popup.CanOverrideSource);
        Assert.Equal(PopupText.FontFamilyFor("en"), _popup.TranslationFontFamily);
        Assert.Equal(PopupText.FontFamilyFor("zh"), _popup.OriginalFontFamily);
    }

    [Fact]
    public void Result_ExplicitAnchorOverridesLoadingAnchor()
    {
        var other = new PopupRect(-800, 50, 200, 100);
        _popup.ShowOcrLoading(Selection);

        _popup.ShowOcrResult(TwoParagraphs, other);

        Assert.Equal(other, _popup.AnchorRect);
    }

    [Fact]
    public void Result_WithoutLoading_DoesNotReuseOldAnchor()
    {
        _popup.ShowOcrLoading(Selection);
        _popup.ShowOcrResult(TwoParagraphs);
        _popup.ShowResult(new PopupResult("你好", "en", "zh"));

        _popup.ShowOcrResult(TwoParagraphs);

        Assert.Null(_popup.AnchorRect);
    }

    [Fact]
    public void ToggleOriginal_ExpandCollapse()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);

        _popup.ToggleOriginal();
        Assert.True(_popup.IsOriginalExpanded);
        Assert.Contains(nameof(PopupViewModel.IsOriginalExpanded), _changed);

        _popup.ToggleOriginal();
        Assert.False(_popup.IsOriginalExpanded);
    }

    [Fact]
    public void Expanded_KeptWithinSameEvenAfterCopyPinHover()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);
        _popup.ToggleOriginal();

        _popup.RequestCopy();
        _popup.RequestCopyOriginal();
        _popup.TogglePin();
        _popup.SetHovered(true);
        _popup.SetHovered(false);
        _popup.TogglePin();
        Advance(1500);

        Assert.True(_popup.IsOriginalExpanded);
    }

    [Fact]
    public void Expanded_KeptAfterCloseAndShowLast()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);
        _popup.ToggleOriginal();
        _popup.Close();

        Assert.True(_popup.ShowLast());
        Assert.True(_popup.IsOriginalExpanded);
    }

    [Fact]
    public void Expanded_ResetByNextTranslation()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);
        _popup.ToggleOriginal();

        _popup.ShowOcrLoading(Selection);
        Assert.False(_popup.IsOriginalExpanded);
        _popup.ShowOcrResult(TwoParagraphs);
        Assert.False(_popup.IsOriginalExpanded);

        _popup.ToggleOriginal();
        _popup.ShowOcrResult(TwoParagraphs, Selection);
        Assert.False(_popup.IsOriginalExpanded);
    }

    [Fact]
    public void ToggleOriginal_IgnoredWithoutOriginal()
    {
        _popup.ToggleOriginal();
        Assert.False(_popup.IsOriginalExpanded);

        _popup.ShowResult(new PopupResult("你好", "en", "zh"));
        _popup.ToggleOriginal();
        Assert.False(_popup.IsOriginalExpanded);
    }

    // ---- 复制 ----

    [Fact]
    public void CopyTranslation_And_CopyOriginal_Content()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);

        _popup.RequestCopy();
        _popup.RequestCopyOriginal();

        Assert.Equal([TwoParagraphs.TranslationText], _copiedTranslation);
        Assert.Equal([TwoParagraphs.SourceText], _copiedOriginal);
    }

    [Fact]
    public void CopyOriginal_WorksWhileCollapsed()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);

        _popup.RequestCopyOriginal();

        Assert.False(_popup.IsOriginalExpanded);
        Assert.Single(_copiedOriginal);
    }

    [Fact]
    public void CopyFeedback_PerButton_ClearsAfterDuration()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);

        _popup.RequestCopyOriginal();
        Assert.True(_popup.ShowOriginalCopiedFeedback);
        Assert.False(_popup.ShowCopiedFeedback);

        _popup.RequestCopy();
        Assert.True(_popup.ShowCopiedFeedback);
        Assert.False(_popup.ShowOriginalCopiedFeedback);

        Advance(1000);
        Assert.False(_popup.ShowCopiedFeedback);
        Assert.False(_popup.ShowOriginalCopiedFeedback);
    }

    [Fact]
    public void CopyOriginal_IgnoredForTextModeAndErrors()
    {
        _popup.ShowResult(new PopupResult("你好", "en", "zh"));
        _popup.RequestCopyOriginal();
        Assert.False(_popup.CanCopyOriginal);

        _popup.ShowOcrLoading(Selection);
        _popup.ShowError(new PopupError(PopupErrorKind.OcrUnavailable));
        _popup.RequestCopyOriginal();
        _popup.RequestCopy();

        Assert.Empty(_copiedOriginal);
        Assert.Empty(_copiedTranslation);
    }

    // ---- 空结果 ----

    [Fact]
    public void Empty_NotAnError()
    {
        _popup.ShowOcrLoading(Selection);
        _popup.ShowOcrResult(PopupOcrResult.Empty("en") with { Elapsed = TimeSpan.FromMilliseconds(150) });

        Assert.Equal(PopupKind.Empty, _popup.Kind);
        Assert.Null(_popup.Error);
        Assert.False(_popup.CanRetry);
        Assert.False(_popup.CanCopy);
        Assert.False(_popup.CanCopyOriginal);
        Assert.False(_popup.HasOriginal);
        Assert.Equal(string.Empty, _popup.LanguageLabel);
        Assert.Equal("150 ms", _popup.ElapsedText);
        Assert.True(_popup.IsVisible);
        Assert.Equal("未识别到文字", PopupText.OcrEmptyText);
    }

    [Fact]
    public void Empty_WhitespaceParagraphs()
    {
        _popup.ShowOcrResult(new PopupOcrResult([" ", "\n"], ["", ""], null, "en"), Selection);

        Assert.Equal(PopupKind.Empty, _popup.Kind);
    }

    [Fact]
    public void Empty_AutoHides()
    {
        _popup.ShowOcrResult(PopupOcrResult.Empty("en"), Selection);

        Advance(8000);

        Assert.False(_popup.IsVisible);
    }

    // ---- 错误态 ----

    [Fact]
    public void Error_KeepsOcrModeAndAnchor_ClearsOriginal()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);
        _popup.ToggleOriginal();

        _popup.ShowOcrLoading(Selection);
        _popup.ShowError(new PopupError(PopupErrorKind.ImageTooLarge));

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.Equal(Selection, _popup.AnchorRect);
        Assert.False(_popup.HasOriginal);
        Assert.False(_popup.IsOriginalExpanded);
        Assert.False(_popup.CanRetry);
        Assert.Equal("选区过大，请缩小选区后重新框选", _popup.ErrorMessage);
    }

    [Fact]
    public void Error_OcrUnavailable_CanRetry()
    {
        _popup.ShowOcrLoading(Selection);
        _popup.ShowError(new PopupError(PopupErrorKind.OcrUnavailable) { MissingModels = ["det"] });
        var retries = 0;
        _popup.RetryRequested += (_, _) => retries++;

        _popup.RequestRetry();

        Assert.Equal(1, retries);
        Assert.Equal("OCR 模型未安装：det", _popup.ErrorMessage);
    }

    [Fact]
    public void Error_WithOcrMode_FromTextState_SwitchesModeAndRepositions()
    {
        _popup.ShowLoading("hello");
        Advance(300);
        _shown.Clear();

        _popup.ShowError(new PopupError(PopupErrorKind.Timeout), PopupContentMode.Ocr, Selection);

        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.Equal(Selection, _popup.AnchorRect);
        Assert.Equal([true], _shown);
    }

    [Fact]
    public void Error_WithTextMode_AfterOcr_ClearsAnchor()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);

        _popup.ShowError(new PopupError(PopupErrorKind.Timeout), PopupContentMode.Text, Selection);

        Assert.Equal(PopupContentMode.Text, _popup.Mode);
        Assert.Null(_popup.AnchorRect);
    }

    [Fact]
    public void Error_WithOcrMode_SameAnchor_KeepsPosition()
    {
        _popup.ShowOcrLoading(Selection);
        Advance(300);
        _shown.Clear();

        _popup.ShowError(new PopupError(PopupErrorKind.Timeout), PopupContentMode.Ocr, Selection);

        Assert.DoesNotContain(true, _shown);
        Assert.Equal(PopupKind.Error, _popup.Kind);
    }

    [Fact]
    public void RetryAvailability_PerMode_HidesRetryAndRaisesCanRetry()
    {
        _popup.ShowOcrLoading(Selection);
        _popup.ShowError(new PopupError(PopupErrorKind.Timeout));
        var retries = 0;
        _popup.RetryRequested += (_, _) => retries++;
        Assert.True(_popup.CanRetry); // 默认可用
        _changed.Clear();

        _popup.SetRetryAvailability(text: true, ocr: false);

        Assert.False(_popup.CanRetry);
        Assert.Contains(nameof(PopupViewModel.CanRetry), _changed);
        _popup.RequestRetry();
        Assert.Equal(0, retries);

        _popup.ShowError(new PopupError(PopupErrorKind.Timeout), PopupContentMode.Text);
        Assert.True(_popup.CanRetry); // 文本模式有上一次文本

        _changed.Clear();
        _popup.SetRetryAvailability(text: true, ocr: false);
        Assert.DoesNotContain(nameof(PopupViewModel.CanRetry), _changed); // 未变化不通知
    }

    [Fact]
    public void Error_ThenResult_ErrorCleared()
    {
        _popup.ShowOcrLoading(Selection);
        _popup.ShowError(new PopupError(PopupErrorKind.InvalidImage));
        _popup.ShowOcrLoading(Selection);
        _popup.ShowOcrResult(TwoParagraphs);

        Assert.Null(_popup.Error);
        Assert.Equal(PopupKind.Result, _popup.Kind);
    }

    [Fact]
    public void Preparing_WithAnchor_IsOcrMode()
    {
        _popup.ShowPreparing(Selection);

        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.Equal(Selection, _popup.AnchorRect);

        _popup.ShowPreparing();
        Assert.Equal(PopupContentMode.Text, _popup.Mode);
        Assert.Null(_popup.AnchorRect);
    }

    // ---- 与 M2 复制翻译互不影响 ----

    [Fact]
    public void TextResult_AfterOcr_ClearsOcrState()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);
        _popup.ToggleOriginal();

        _popup.ShowLoading("hello");
        Assert.Equal(PopupContentMode.Text, _popup.Mode);
        Assert.Null(_popup.AnchorRect);
        _popup.ShowResult(new PopupResult("你好", "en", "zh") { SourceDetected = true });

        Assert.False(_popup.HasOriginal);
        Assert.Equal(string.Empty, _popup.OriginalText);
        Assert.False(_popup.IsOriginalExpanded);
        Assert.True(_popup.CanOverrideSource);
        Assert.Equal("你好", _popup.Translation);
    }

    [Fact]
    public void OcrResult_AfterText_Repositions()
    {
        _popup.ShowResult(new PopupResult("你好", "en", "zh"));
        _shown.Clear();

        _popup.ShowOcrResult(TwoParagraphs, Selection);

        Assert.Equal([true], _shown);
    }

    [Fact]
    public void Pinned_OcrResult_UpdatesInPlace()
    {
        _popup.ShowOcrResult(TwoParagraphs, Selection);
        _popup.TogglePin();
        _shown.Clear();

        _popup.ShowOcrLoading(new PopupRect(0, 0, 50, 50));
        _popup.ShowOcrResult(TwoParagraphs);

        Assert.DoesNotContain(true, _shown);
        Assert.True(_popup.IsPinned);
    }

    [Fact]
    public void ShowOcrResult_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _popup.ShowOcrResult(null!));
    }
}
