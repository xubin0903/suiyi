using System.ComponentModel;
using System.Runtime.CompilerServices;
using Suiyi.Core.Tray;

namespace Suiyi.Core.Popup;

/// <summary>
/// 译文浮窗的 ViewModel / 状态机。只能在 UI 线程上使用；计时器回调经构造参数 <c>dispatch</c> 切回 UI 线程。
/// 集成方调用 <see cref="ShowPreparing"/>、<see cref="ShowLoading"/>、<see cref="ShowResult"/>、<see cref="ShowError"/>，
/// 并订阅 <see cref="CopyTranslationRequested"/>、<see cref="RetryRequested"/>、<see cref="SourceLanguageOverride"/>、<see cref="Closed"/>。
/// 窗口订阅 <see cref="Shown"/>、<see cref="Closed"/> 与 <see cref="PropertyChanged"/>。
/// </summary>
public sealed class PopupViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly OneShotTimer _loadingTimer;
    private readonly OneShotTimer _autoHideTimer;
    private readonly OneShotTimer _copiedTimer;
    private bool _positioned;

    private PopupKind _kind;
    private bool _isVisible;
    private bool _isPinned;
    private bool _isHovered;
    private bool _showLoadingIndicator;
    private bool _showCopiedFeedback;
    private string _sourcePreview = string.Empty;
    private string _translation = string.Empty;
    private string _languageLabel = string.Empty;
    private string? _elapsedText;
    private PopupError? _error;
    private string _translationFontFamily = PopupText.FontFamilyFor(null);

    /// <summary>创建浮窗 ViewModel。</summary>
    /// <param name="options">参数；默认 <see cref="PopupOptions"/>。</param>
    /// <param name="timeProvider">时钟，测试时注入。</param>
    /// <param name="dispatch">把计时器回调切回 UI 线程；默认直接调用（测试用）。</param>
    public PopupViewModel(PopupOptions? options = null, TimeProvider? timeProvider = null, Action<Action>? dispatch = null)
    {
        Options = options ?? new PopupOptions();
        var clock = timeProvider ?? TimeProvider.System;
        var post = dispatch ?? (a => a());
        _loadingTimer = new OneShotTimer(clock, post);
        _autoHideTimer = new OneShotTimer(clock, post);
        _copiedTimer = new OneShotTimer(clock, post);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>窗口应显示（或在已显示时按参数重新定位）。</summary>
    public event EventHandler<PopupShownEventArgs>? Shown;

    /// <summary>浮窗已关闭（用户、自动消失或程序）。</summary>
    public event EventHandler<PopupClosedEventArgs>? Closed;

    /// <summary>用户点击「复制」。集成方用 <c>ClipboardWriter.SetText</c> 写入，避免自触发。</summary>
    public event EventHandler<PopupCopyEventArgs>? CopyTranslationRequested;

    /// <summary>用户点击「重试」。</summary>
    public event EventHandler? RetryRequested;

    /// <summary>用户在语种标签上手动指定原文语种，集成方应以该语种重新翻译。</summary>
    public event EventHandler<PopupSourceOverrideEventArgs>? SourceLanguageOverride;

    /// <summary>参数。</summary>
    public PopupOptions Options { get; }

    /// <summary>当前内容类别。</summary>
    public PopupKind Kind { get => _kind; private set => Set(ref _kind, value); }

    /// <summary>窗口是否应可见。</summary>
    public bool IsVisible { get => _isVisible; private set => Set(ref _isVisible, value); }

    /// <summary>是否已钉住（不自动消失、可拖动、可激活以选中文字）。</summary>
    public bool IsPinned { get => _isPinned; private set => Set(ref _isPinned, value); }

    /// <summary>鼠标是否悬停在浮窗上（悬停时暂停自动消失计时）。</summary>
    public bool IsHovered { get => _isHovered; private set => Set(ref _isHovered, value); }

    /// <summary>Loading 状态下是否显示加载指示（进入 Loading 后延迟 <see cref="PopupOptions.LoadingIndicatorDelay"/>）。</summary>
    public bool ShowLoadingIndicator { get => _showLoadingIndicator; private set => Set(ref _showLoadingIndicator, value); }

    /// <summary>是否显示「已复制」反馈。</summary>
    public bool ShowCopiedFeedback { get => _showCopiedFeedback; private set => Set(ref _showCopiedFeedback, value); }

    /// <summary>原文摘要（Loading）。</summary>
    public string SourcePreview { get => _sourcePreview; private set => Set(ref _sourcePreview, value); }

    /// <summary>译文（Result）。</summary>
    public string Translation { get => _translation; private set => Set(ref _translation, value); }

    /// <summary>语种标签（Result），例如「中文 → English」。</summary>
    public string LanguageLabel { get => _languageLabel; private set => Set(ref _languageLabel, value); }

    /// <summary>耗时小字（Result，可空）。</summary>
    public string? ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    /// <summary>当前错误（Error）。</summary>
    public PopupError? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                OnPropertyChanged(nameof(ErrorMessage));
                OnPropertyChanged(nameof(CanRetry));
            }
        }
    }

    /// <summary>错误提示（Error）。</summary>
    public string ErrorMessage => _error?.Message ?? string.Empty;

    /// <summary>是否显示「重试」。</summary>
    public bool CanRetry => _kind == PopupKind.Error && _error is { CanRetry: true };

    /// <summary>是否可以复制（有译文）。</summary>
    public bool CanCopy => _kind == PopupKind.Result && _translation.Length > 0;

    /// <summary>译文字体回退链（按目标语种）。</summary>
    public string TranslationFontFamily { get => _translationFontFamily; private set => Set(ref _translationFontFamily, value); }

    /// <summary>服务未就绪时触发了翻译：立即显示「正在准备翻译服务…」。</summary>
    public void ShowPreparing()
    {
        BeginSession();
        SetKind(PopupKind.Preparing);
        Present();
    }

    /// <summary>
    /// 开始一次翻译：显示原文摘要。加载指示在 <see cref="PopupOptions.LoadingIndicatorDelay"/> 后才出现；
    /// 浮窗原本隐藏时整个窗口也等到那时才显示，结果先到则直接显示结果，不闪烁。
    /// </summary>
    public void ShowLoading(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        BeginSession();
        SetKind(PopupKind.Loading);
        SourcePreview = PopupText.SourcePreview(sourceText);
        ShowLoadingIndicator = false;
        _autoHideTimer.Stop();
        _loadingTimer.Start(Options.LoadingIndicatorDelay, () =>
        {
            ShowLoadingIndicator = true;
            Present();
        });

        if (IsVisible)
        {
            Present();
        }
    }

    /// <summary>显示译文。</summary>
    public void ShowResult(PopupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ContinueOrBeginSession();
        LanguageLabel = PopupText.LanguageLabel(result.Source, result.Target, result.SourceDetected);
        ElapsedText = result.Elapsed is { } elapsed ? PopupText.FormatElapsed(elapsed) : null;
        TranslationFontFamily = PopupText.FontFamilyFor(result.Target);
        Translation = result.Translation;
        SetKind(PopupKind.Result);
        Present();
    }

    /// <summary>显示错误。</summary>
    public void ShowError(PopupError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        ContinueOrBeginSession();
        Error = error;
        SetKind(PopupKind.Error);
        Present();
    }

    /// <summary>重新显示上一次的内容（托盘左键）。从未显示过时返回 <see langword="false"/>。</summary>
    public bool ShowLast()
    {
        if (Kind == PopupKind.None)
        {
            return false;
        }

        if (!IsVisible)
        {
            _positioned = false;
        }

        if (Kind == PopupKind.Loading)
        {
            ShowLoadingIndicator = true;
        }

        Present();
        return true;
    }

    /// <summary>关闭（隐藏）浮窗并取消钉住。内容保留，可 <see cref="ShowLast"/>。</summary>
    public void Close(PopupCloseReason reason = PopupCloseReason.User)
    {
        _loadingTimer.Stop();
        _autoHideTimer.Stop();
        _copiedTimer.Stop();
        ShowCopiedFeedback = false;
        IsHovered = false;
        IsPinned = false;
        if (!IsVisible)
        {
            return;
        }

        IsVisible = false;
        Closed?.Invoke(this, new PopupClosedEventArgs(reason));
    }

    /// <summary>切换钉住。钉住后不自动消失；取消钉住后重新计时。</summary>
    public void TogglePin()
    {
        if (!IsVisible)
        {
            return;
        }

        IsPinned = !IsPinned;
        RestartAutoHide();
    }

    /// <summary>鼠标进入 / 离开浮窗。进入时暂停计时，离开后重新计时。</summary>
    public void SetHovered(bool hovered)
    {
        IsHovered = hovered;
        RestartAutoHide();
    }

    /// <summary>点击「复制」：发 <see cref="CopyTranslationRequested"/> 并显示「已复制」。</summary>
    public void RequestCopy()
    {
        if (!CanCopy)
        {
            return;
        }

        CopyTranslationRequested?.Invoke(this, new PopupCopyEventArgs(Translation));
        ShowCopiedFeedback = true;
        _copiedTimer.Start(Options.CopiedFeedbackDuration, () => ShowCopiedFeedback = false);
    }

    /// <summary>点击「重试」。</summary>
    public void RequestRetry()
    {
        if (CanRetry)
        {
            RetryRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>在语种标签上指定原文语种（zh / en / ja）。</summary>
    public void RequestSourceOverride(string language)
    {
        if (!TrayLanguages.IsSupported(language))
        {
            return;
        }

        SourceLanguageOverride?.Invoke(this, new PopupSourceOverrideEventArgs(language.ToLowerInvariant()));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loadingTimer.Dispose();
        _autoHideTimer.Dispose();
        _copiedTimer.Dispose();
    }

    private void BeginSession()
    {
        _positioned = false;
        _loadingTimer.Stop();
        _copiedTimer.Stop();
        ShowCopiedFeedback = false;
    }

    private void ContinueOrBeginSession()
    {
        if (Kind == PopupKind.Loading)
        {
            _loadingTimer.Stop();
            ShowLoadingIndicator = false;
            return;
        }

        BeginSession();
    }

    private void SetKind(PopupKind kind)
    {
        Kind = kind;
        if (kind != PopupKind.Result)
        {
            Translation = string.Empty;
        }

        if (kind != PopupKind.Error)
        {
            Error = null;
        }

        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCopy));
    }

    private void Present()
    {
        var reposition = !_positioned && !IsPinned;
        var becameVisible = !IsVisible;
        _positioned = true;
        IsVisible = true;
        if (becameVisible || reposition)
        {
            Shown?.Invoke(this, new PopupShownEventArgs(reposition));
        }

        RestartAutoHide();
    }

    private void RestartAutoHide()
    {
        if (Options.AutoHideSeconds <= 0 || !IsVisible || IsPinned || IsHovered || Kind == PopupKind.Loading)
        {
            _autoHideTimer.Stop();
            return;
        }

        _autoHideTimer.Start(TimeSpan.FromSeconds(Options.AutoHideSeconds), () => Close(PopupCloseReason.AutoHide));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
