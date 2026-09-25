namespace Suiyi.Core.Popup;

/// <summary><c>--popup-demo</c>：依次展示各状态，便于截图与手测。</summary>
public static class PopupDemo
{
    /// <summary>每一步的间隔。</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(3);

    /// <summary>步骤数（一轮）。</summary>
    public static int StepCount => Steps.Length;

    private static readonly Action<PopupViewModel>[] Steps =
    [
        p => p.ShowPreparing(),
        p => p.ShowLoading("The quick brown fox jumps over the lazy dog. Local, offline and free."),
        p => p.ShowResult(new PopupResult("敏捷的棕色狐狸跳过了那只懒狗。本地、离线、免费。", "en", "zh") { SourceDetected = true, Elapsed = TimeSpan.FromMilliseconds(320) }),
        p => p.ShowResult(new PopupResult("Suiyi is a free, open-source local translator that runs entirely on your computer.", "zh", "en") { Elapsed = TimeSpan.FromMilliseconds(1450) }),
        p => p.ShowResult(new PopupResult("随訳はパソコン上で動作する無料のオープンソース翻訳ツールです。", "zh", "ja") { SourceDetected = true, Elapsed = TimeSpan.FromMilliseconds(680) }),
        p => p.ShowResult(new PopupResult(string.Join("\n\n", Enumerable.Repeat("这是一段很长的译文，用来检查浮窗的最大宽度、自动换行和滚动条。长文本不应超出屏幕工作区的一半高度。", 12)), "en", "zh") { SourceDetected = true, Elapsed = TimeSpan.FromSeconds(2.4) }),
        p => p.ShowError(new PopupError(PopupErrorKind.ServiceUnavailable)),
        p => p.ShowError(new PopupError(PopupErrorKind.EngineStartTimeout)),
        p => p.ShowError(new PopupError(PopupErrorKind.Timeout)),
        p => p.ShowError(new PopupError(PopupErrorKind.MissingModels) { MissingModels = ["opus-mt-en-zh", "opus-mt-ja-en"] }),
        p => p.ShowError(new PopupError(PopupErrorKind.DetectFailed)),
        p => p.ShowError(new PopupError(PopupErrorKind.TextTooLong) { Limit = 5000, Length = 6234 }),
    ];

    /// <summary>执行第 <paramref name="step"/> 步（按一轮取模）。</summary>
    public static void ApplyStep(PopupViewModel popup, int step)
    {
        ArgumentNullException.ThrowIfNull(popup);
        Steps[((step % Steps.Length) + Steps.Length) % Steps.Length](popup);
    }
}
