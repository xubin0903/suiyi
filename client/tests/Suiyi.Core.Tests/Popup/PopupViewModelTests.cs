using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Popup;

public sealed class PopupViewModelTests : IDisposable
{
    private static readonly PopupResult Sample = new("你好", "en", "zh") { SourceDetected = true, Elapsed = TimeSpan.FromMilliseconds(320) };

    private readonly FakeTimeProvider _clock = new();
    private readonly PopupViewModel _popup;
    private readonly List<bool> _shown = [];
    private readonly List<PopupCloseReason> _closed = [];

    public PopupViewModelTests()
    {
        _popup = new PopupViewModel(timeProvider: _clock);
        _popup.Shown += (_, e) => _shown.Add(e.Reposition);
        _popup.Closed += (_, e) => _closed.Add(e.Reason);
    }

    public void Dispose() => _popup.Dispose();

    private void Advance(double ms) => _clock.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void Initial_HiddenNone()
    {
        Assert.Equal(PopupKind.None, _popup.Kind);
        Assert.False(_popup.IsVisible);
        Assert.False(_popup.ShowLast());
        Assert.Empty(_shown);
    }

    [Fact]
    public void Defaults()
    {
        Assert.Equal(480, _popup.Options.MaxWidth);
        Assert.Equal(8, _popup.Options.AutoHideSeconds);
        Assert.Equal(16, _popup.Options.CursorOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(300), _popup.Options.LoadingIndicatorDelay);
        Assert.Equal(TimeSpan.FromSeconds(1), _popup.Options.CopiedFeedbackDuration);
    }

    [Fact]
    public void Preparing_ShowsImmediately()
    {
        _popup.ShowPreparing();

        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.True(_popup.IsVisible);
        Assert.Equal([true], _shown);
    }

    // ---- Loading 300 ms ----

    [Fact]
    public void Loading_WindowAndIndicatorAppearAfter300ms()
    {
        _popup.ShowLoading("Hello\nworld");

        Assert.Equal(PopupKind.Loading, _popup.Kind);
        Assert.Equal("Hello world", _popup.SourcePreview);
        Assert.False(_popup.IsVisible);
        Assert.False(_popup.ShowLoadingIndicator);

        Advance(299);
        Assert.False(_popup.IsVisible);

        Advance(1);
        Assert.True(_popup.IsVisible);
        Assert.True(_popup.ShowLoadingIndicator);
        Assert.Equal([true], _shown);
    }

    [Fact]
    public void FastResult_NoLoadingFlash()
    {
        _popup.ShowLoading("Hello");
        Advance(200);
        _popup.ShowResult(Sample);
        Advance(500);

        Assert.Equal(PopupKind.Result, _popup.Kind);
        Assert.False(_popup.ShowLoadingIndicator);
        Assert.Equal([true], _shown);
    }

    [Fact]
    public void SlowResult_IndicatorThenResultSamePosition()
    {
        _popup.ShowLoading("Hello");
        Advance(300);
        _popup.ShowResult(Sample);

        Assert.False(_popup.ShowLoadingIndicator);
        Assert.Equal("你好", _popup.Translation);
        Assert.Equal([true], _shown); // 结果不再重新定位
    }

    [Fact]
    public void Loading_WhenAlreadyVisible_UpdatesAndRepositionsImmediately()
    {
        _popup.ShowResult(Sample);
        _popup.ShowLoading("next");

        Assert.True(_popup.IsVisible);
        Assert.Equal(PopupKind.Loading, _popup.Kind);
        Assert.False(_popup.ShowLoadingIndicator);
        Assert.Equal(string.Empty, _popup.Translation);
        Assert.Equal([true, true], _shown);

        Advance(300);
        Assert.True(_popup.ShowLoadingIndicator);
        Assert.Equal([true, true], _shown);
    }

    [Fact]
    public void Loading_DoesNotAutoHide()
    {
        _popup.ShowLoading("x");
        Advance(60_000);

        Assert.True(_popup.IsVisible);
        Assert.Empty(_closed);
    }

    [Fact]
    public void Loading_ReplacedByNewLoading_RestartsDelay()
    {
        _popup.ShowLoading("a");
        Advance(200);
        _popup.ShowLoading("b");
        Advance(200);

        Assert.False(_popup.IsVisible);
        Advance(100);
        Assert.True(_popup.IsVisible);
        Assert.Equal("b", _popup.SourcePreview);
    }

    // ---- Result / Error ----

    [Fact]
    public void Result_SetsTextLabelElapsedFont()
    {
        _popup.ShowResult(new PopupResult("Hello", "zh", "en") { Elapsed = TimeSpan.FromSeconds(1.45) });

        Assert.Equal("Hello", _popup.Translation);
        Assert.Equal("中文 → English", _popup.LanguageLabel);
        Assert.Equal("1.5 s", _popup.ElapsedText);
        Assert.StartsWith("Segoe UI", _popup.TranslationFontFamily, StringComparison.Ordinal);
        Assert.True(_popup.CanCopy);
        Assert.False(_popup.CanRetry);
        Assert.Null(_popup.Error);
    }

    [Fact]
    public void Result_WithoutElapsed()
    {
        _popup.ShowResult(new PopupResult("x", "zh", "en"));

        Assert.Null(_popup.ElapsedText);
    }

    [Fact]
    public void Error_SetsMessageAndRetry()
    {
        _popup.ShowResult(Sample);
        _popup.ShowError(new PopupError(PopupErrorKind.Timeout));

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal("翻译超时，请重试", _popup.ErrorMessage);
        Assert.True(_popup.CanRetry);
        Assert.False(_popup.CanCopy);
        Assert.Equal(string.Empty, _popup.Translation);
    }

    [Fact]
    public void TextTooLong_NoRetry()
    {
        var retries = 0;
        _popup.RetryRequested += (_, _) => retries++;
        _popup.ShowError(new PopupError(PopupErrorKind.TextTooLong));
        _popup.RequestRetry();

        Assert.False(_popup.CanRetry);
        Assert.Equal(0, retries);
    }

    [Fact]
    public void Retry_RaisedOnlyInError()
    {
        var retries = 0;
        _popup.RetryRequested += (_, _) => retries++;

        _popup.RequestRetry();
        _popup.ShowResult(Sample);
        _popup.RequestRetry();
        _popup.ShowError(new PopupError(PopupErrorKind.ServiceUnavailable));
        _popup.RequestRetry();

        Assert.Equal(1, retries);
    }

    [Fact]
    public void NewResultWithoutLoading_IsNewSession()
    {
        _popup.ShowResult(Sample);
        _popup.ShowResult(Sample with { Translation = "再见" });

        Assert.Equal([true, true], _shown);
        Assert.Equal("再见", _popup.Translation);
    }

    // ---- 自动消失 ----

    [Fact]
    public void Result_AutoHidesAfter8s()
    {
        _popup.ShowResult(Sample);
        Advance(7_999);
        Assert.True(_popup.IsVisible);

        Advance(1);
        Assert.False(_popup.IsVisible);
        Assert.Equal([PopupCloseReason.AutoHide], _closed);
    }

    [Fact]
    public void Error_AlsoAutoHides()
    {
        _popup.ShowError(new PopupError(PopupErrorKind.ServiceUnavailable));
        Advance(8_000);

        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public void Preparing_AutoHides()
    {
        _popup.ShowPreparing();
        Advance(8_000);

        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public void AutoHide_CountsFromResultNotFromLoading()
    {
        _popup.ShowLoading("x");
        Advance(5_000);
        _popup.ShowResult(Sample);
        Advance(7_000);

        Assert.True(_popup.IsVisible);
        Advance(1_000);
        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public void Hover_PausesAndLeaveRestartsFullDuration()
    {
        _popup.ShowResult(Sample);
        Advance(7_000);
        _popup.SetHovered(true);
        Advance(60_000);
        Assert.True(_popup.IsVisible);

        _popup.SetHovered(false);
        Advance(7_999);
        Assert.True(_popup.IsVisible);
        Advance(1);
        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public void Pinned_NeverAutoHides_UnpinRestarts()
    {
        _popup.ShowResult(Sample);
        _popup.TogglePin();
        Assert.True(_popup.IsPinned);
        Advance(60_000);
        Assert.True(_popup.IsVisible);

        _popup.TogglePin();
        Assert.False(_popup.IsPinned);
        Advance(8_000);
        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public void AutoHideZero_Disabled()
    {
        using var popup = new PopupViewModel(new PopupOptions { AutoHideSeconds = 0 }, _clock);
        popup.ShowResult(Sample);
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.True(popup.IsVisible);
    }

    [Fact]
    public void NewContent_RestartsAutoHide()
    {
        _popup.ShowResult(Sample);
        Advance(6_000);
        _popup.ShowError(new PopupError(PopupErrorKind.Timeout));
        Advance(6_000);

        Assert.True(_popup.IsVisible);
    }

    [Fact]
    public void TogglePin_WhenHidden_Ignored()
    {
        _popup.TogglePin();

        Assert.False(_popup.IsPinned);
    }

    // ---- 钉住 ----

    [Fact]
    public void Pinned_NewTranslationUpdatesInPlace()
    {
        _popup.ShowResult(Sample);
        _popup.TogglePin();
        _popup.ShowLoading("next");
        Advance(300);
        _popup.ShowResult(Sample with { Translation = "下一条" });

        Assert.True(_popup.IsPinned);
        Assert.Equal("下一条", _popup.Translation);
        Assert.Equal([true], _shown); // 钉住后不再重新定位
    }

    // ---- 关闭 ----

    [Fact]
    public void Close_HidesUnpinsAndRaisesOnce()
    {
        _popup.ShowResult(Sample);
        _popup.TogglePin();
        _popup.Close();
        _popup.Close();

        Assert.False(_popup.IsVisible);
        Assert.False(_popup.IsPinned);
        Assert.Equal([PopupCloseReason.User], _closed);
        Assert.Equal(PopupKind.Result, _popup.Kind); // 内容保留
    }

    [Fact]
    public void Close_DuringDelayedLoading_CancelsAppearance()
    {
        _popup.ShowLoading("x");
        _popup.Close();
        Advance(1_000);

        Assert.False(_popup.IsVisible);
        Assert.Empty(_closed);
    }

    [Fact]
    public void ShowLast_ReshowsWithRepositionAndTimer()
    {
        _popup.ShowResult(Sample);
        _popup.Close();

        Assert.True(_popup.ShowLast());
        Assert.True(_popup.IsVisible);
        Assert.Equal("你好", _popup.Translation);
        Assert.Equal([true, true], _shown);

        Advance(8_000);
        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public void ShowLast_WhileVisible_NoReposition()
    {
        _popup.ShowResult(Sample);
        _popup.ShowLast();

        Assert.Equal([true], _shown);
    }

    [Fact]
    public void ShowLast_LoadingShowsIndicatorImmediately()
    {
        _popup.ShowLoading("x");
        _popup.ShowLast();

        Assert.True(_popup.IsVisible);
        Assert.True(_popup.ShowLoadingIndicator);
    }

    // ---- 复制 / 语种 ----

    [Fact]
    public void Copy_RaisesAndShowsFeedbackFor1s()
    {
        var copied = new List<string>();
        _popup.CopyTranslationRequested += (_, e) => copied.Add(e.Text);
        _popup.ShowResult(Sample);

        _popup.RequestCopy();
        Assert.Equal(["你好"], copied);
        Assert.True(_popup.ShowCopiedFeedback);

        Advance(999);
        Assert.True(_popup.ShowCopiedFeedback);
        Advance(1);
        Assert.False(_popup.ShowCopiedFeedback);
    }

    [Fact]
    public void Copy_IgnoredWithoutResult()
    {
        var copied = 0;
        _popup.CopyTranslationRequested += (_, _) => copied++;

        _popup.RequestCopy();
        _popup.ShowError(new PopupError(PopupErrorKind.Timeout));
        _popup.RequestCopy();
        _popup.ShowResult(Sample with { Translation = string.Empty });
        _popup.RequestCopy();

        Assert.Equal(0, copied);
        Assert.False(_popup.ShowCopiedFeedback);
    }

    [Fact]
    public void Copy_FeedbackClearedByNewTranslation()
    {
        _popup.ShowResult(Sample);
        _popup.RequestCopy();
        _popup.ShowLoading("next");

        Assert.False(_popup.ShowCopiedFeedback);
    }

    [Theory]
    [InlineData("JA", "ja")]
    [InlineData("zh", "zh")]
    [InlineData("en", "en")]
    public void SourceOverride_Raised(string input, string expected)
    {
        var languages = new List<string>();
        _popup.SourceLanguageOverride += (_, e) => languages.Add(e.Language);

        _popup.RequestSourceOverride(input);

        Assert.Equal([expected], languages);
    }

    [Fact]
    public void SourceOverride_UnsupportedIgnored()
    {
        var raised = 0;
        _popup.SourceLanguageOverride += (_, _) => raised++;

        _popup.RequestSourceOverride("fr");

        Assert.Equal(0, raised);
    }

    // ---- 通知与调度 ----

    [Fact]
    public void PropertyChanged_RaisedForDerivedProperties()
    {
        var names = new HashSet<string?>();
        _popup.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        _popup.ShowError(new PopupError(PopupErrorKind.Timeout));

        Assert.Contains(nameof(PopupViewModel.Kind), names);
        Assert.Contains(nameof(PopupViewModel.ErrorMessage), names);
        Assert.Contains(nameof(PopupViewModel.CanRetry), names);
        Assert.Contains(nameof(PopupViewModel.CanCopy), names);
        Assert.Contains(nameof(PopupViewModel.IsVisible), names);
    }

    [Fact]
    public void TimerCallbacks_GoThroughDispatch()
    {
        var queue = new Queue<Action>();
        using var popup = new PopupViewModel(timeProvider: _clock, dispatch: queue.Enqueue);
        popup.ShowResult(Sample);

        _clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True(popup.IsVisible);
        Assert.Single(queue);

        queue.Dequeue()();
        Assert.False(popup.IsVisible);
    }

    [Fact]
    public void StaleDispatchedCallback_Ignored()
    {
        var queue = new Queue<Action>();
        using var popup = new PopupViewModel(timeProvider: _clock, dispatch: queue.Enqueue);
        popup.ShowResult(Sample);
        _clock.Advance(TimeSpan.FromSeconds(8));

        // 回调排队期间用户悬停：旧回调必须作废。
        popup.SetHovered(true);
        queue.Dequeue()();

        Assert.True(popup.IsVisible);
    }

    [Fact]
    public void Arguments_Validated()
    {
        Assert.Throws<ArgumentNullException>(() => _popup.ShowLoading(null!));
        Assert.Throws<ArgumentNullException>(() => _popup.ShowResult(null!));
        Assert.Throws<ArgumentNullException>(() => _popup.ShowError(null!));
    }
}
