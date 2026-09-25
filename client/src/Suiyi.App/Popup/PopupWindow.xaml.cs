using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Suiyi.App.Interop;
using Suiyi.Core.Popup;
using static Suiyi.App.Interop.PopupNativeMethods;

namespace Suiyi.App.Popup;

/// <summary>
/// 译文浮窗。无边框、置顶、不进任务栏；未钉住时带 <c>WS_EX_NOACTIVATE</c>，显示时不抢焦点。
/// 位置按光标所在显示器工作区计算（物理像素，Per-Monitor V2）。内容与计时全部来自 <see cref="PopupViewModel"/>。
/// </summary>
internal sealed partial class PopupWindow : Window
{
    private const int EscHotkeyId = 0x5301;
    private const double ShadowMargin = 12;
    private const double SelectionGap = 8;

    private readonly PopupViewModel _model;
    private readonly IntPtr _hwnd;
    private PopupPoint _anchor;
    private bool _hasAnchor;
    private bool _escRegistered;
    private bool _allowClose;

    public PopupWindow(PopupViewModel model)
    {
        _model = model;
        InitializeComponent();
        MaxWidth = model.Options.MaxWidth + (ShadowMargin * 2);

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        SetNoActivate(true);

        _model.PropertyChanged += OnModelPropertyChanged;
        _model.Shown += OnModelShown;
        _model.Closed += OnModelClosed;

        CloseButton.Click += (_, _) => _model.Close(PopupCloseReason.User);
        PinButton.Click += (_, _) => _model.TogglePin();
        CopyButton.Click += (_, _) => _model.RequestCopy();
        CopyOriginalButton.Click += (_, _) => _model.RequestCopyOriginal();
        OriginalToggle.Click += (_, _) => _model.ToggleOriginal();
        RetryButton.Click += (_, _) => _model.RequestRetry();
        LanguageButton.Click += (_, _) => OpenSourceMenu();
        Header.MouseLeftButtonDown += OnHeaderMouseDown;
        MouseEnter += (_, _) => OnHover(true);
        MouseLeave += (_, _) => OnHover(false);
        KeyDown += OnKeyDown;
        SizeChanged += (_, _) => OnSizeChanged();

        Render();
    }

    /// <summary>程序退出时真正关闭窗口（其余情况下关闭只是隐藏）。</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>框选截屏前临时隐藏（不改变浮窗状态），返回是否确实隐藏了。</summary>
    public bool HideForCapture()
    {
        if (!IsVisible)
        {
            return false;
        }

        SetEscHotkey(false);
        Hide();
        return true;
    }

    /// <summary>框选结束后恢复；期间浮窗已被关闭（如自动消失）则保持隐藏。</summary>
    public void RestoreAfterCapture()
    {
        if (_model.IsVisible && !IsVisible)
        {
            SetNoActivate(!_model.IsPinned);
            Show();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Alt+F4 等：只隐藏。
            e.Cancel = true;
            _model.Close(PopupCloseReason.User);
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.PropertyChanged -= OnModelPropertyChanged;
        _model.Shown -= OnModelShown;
        _model.Closed -= OnModelClosed;
        SetEscHotkey(false);
        base.OnClosed(e);
    }

    private void OnModelShown(object? sender, PopupShownEventArgs e)
    {
        ApplyTheme();
        if ((e.Reposition || !_hasAnchor) && GetCursorPos(out var cursor))
        {
            _anchor = new PopupPoint(cursor.X, cursor.Y);
            _hasAnchor = true;
        }

        var reposition = e.Reposition || !IsVisible;
        if (!IsVisible)
        {
            // 先在隐藏状态下挪到光标旁，避免在旧位置闪一下；显示后按实际尺寸再算一次。
            PlaceAtAnchor();
            SetNoActivate(!_model.IsPinned);
            Show();
        }

        if (reposition && !_model.IsPinned)
        {
            PlaceAtAnchor();
        }
    }

    private void OnModelClosed(object? sender, PopupClosedEventArgs e)
    {
        SetEscHotkey(false);
        Hide();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PopupViewModel.IsPinned))
        {
            // 钉住后允许激活（选中文字、Esc）；取消钉住恢复不抢焦点。
            SetNoActivate(!_model.IsPinned);
        }

        Render();
    }

    private void Render()
    {
        var kind = _model.Kind;
        PreparingPanel.Text = PopupText.PreparingText;
        PreparingPanel.Visibility = Vis(kind == PopupKind.Preparing);
        LoadingPanel.Visibility = Vis(kind == PopupKind.Loading);
        ResultPanel.Visibility = Vis(kind == PopupKind.Result);
        ErrorPanel.Visibility = Vis(kind == PopupKind.Error);
        EmptyPanel.Visibility = Vis(kind == PopupKind.Empty);
        EmptyPanel.Text = PopupText.OcrEmptyText;

        OriginalSection.Visibility = Vis(kind == PopupKind.Result && _model.HasOriginal);
        OriginalToggle.Content = _model.IsOriginalExpanded ? PopupText.HideOriginalText : PopupText.ShowOriginalText;
        OriginalText.Visibility = Vis(_model.IsOriginalExpanded);
        OriginalText.Text = _model.OriginalText;
        OriginalText.FontFamily = new FontFamily(_model.OriginalFontFamily);
        CopyOriginalButton.Visibility = Vis(_model.CanCopyOriginal);
        CopyOriginalButton.Content = _model.ShowOriginalCopiedFeedback ? "已复制" : "复制原文";

        SourcePreviewText.Text = _model.SourcePreview;
        LoadingIndicator.Visibility = _model.ShowLoadingIndicator ? Visibility.Visible : Visibility.Hidden;

        TranslationText.Text = _model.Translation;
        TranslationText.FontFamily = new FontFamily(_model.TranslationFontFamily);

        ErrorText.Text = _model.ErrorMessage;
        RetryButton.Visibility = Vis(_model.CanRetry);

        LanguageButton.Content = _model.LanguageLabel;
        var detectFailed = kind == PopupKind.Error && _model.Error?.Kind == PopupErrorKind.DetectFailed;
        LanguageButton.Visibility = Vis(kind == PopupKind.Result || (detectFailed && _model.CanOverrideSource));
        if (detectFailed)
        {
            LanguageButton.Content = "指定原文语种 ▾";
        }

        // 框选翻译的语种标签只展示，不能点（改原文语种需要重新识别，#58 之后再定）。
        LanguageButton.IsHitTestVisible = _model.CanOverrideSource;
        LanguageButton.Cursor = _model.CanOverrideSource ? Cursors.Hand : Cursors.Arrow;
        LanguageButton.ToolTip = _model.CanOverrideSource ? "点击手动指定原文语种" : null;

        ElapsedText.Text = kind == PopupKind.Result ? _model.ElapsedText ?? string.Empty : string.Empty;
        CopyButton.Visibility = Vis(_model.CanCopy);
        var copyLabel = _model.HasOriginal ? "复制译文" : "复制";
        CopyButton.Content = _model.ShowCopiedFeedback ? "已复制" : copyLabel;
        PinButton.IsChecked = _model.IsPinned;
        Header.Cursor = _model.IsPinned ? Cursors.SizeAll : null;
    }

    private void ApplyTheme()
    {
        var theme = PopupTheme.Current();
        Card.Background = theme.Background;
        Card.BorderBrush = theme.Border;
        Foreground = theme.Foreground;
        TranslationText.Foreground = theme.Foreground;
        TranslationText.SelectionBrush = theme.Accent;
        SourcePreviewText.Foreground = theme.Secondary;
        EmptyPanel.Foreground = theme.Secondary;
        OriginalText.Foreground = theme.Secondary;
        OriginalText.SelectionBrush = theme.Accent;
        OriginalToggle.Foreground = theme.Secondary;
        ElapsedText.Foreground = theme.Secondary;
        ErrorText.Foreground = theme.ErrorForeground;
        LoadingIndicator.Foreground = theme.Accent;
        LoadingIndicator.Background = Brushes.Transparent;
        FontFamily = new FontFamily(PopupText.FontFamilyFor(null));
    }

    private void PlaceAtAnchor()
    {
        if (_model.AnchorRect is { } selection)
        {
            PlaceAroundRect(selection);
            return;
        }

        if (!_hasAnchor)
        {
            return;
        }

        var cursor = new PopupNativeMethods.Point { X = (int)_anchor.X, Y = (int)_anchor.Y };
        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        // 先移到光标所在显示器，让 WPF 按该显示器 DPI 重新缩放（WM_DPICHANGED），再按新尺寸计算。
        SetWindowPos(_hwnd, IntPtr.Zero, cursor.X, cursor.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = new PopupRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        ResultPanel.MaxHeight = Math.Max(80, (work.Height / dpi.DpiScaleY / 2) - 60);
        UpdateLayout();

        var size = new PopupSize(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY);
        var offset = Math.Max(0, _model.Options.CursorOffset - ShadowMargin) * dpi.DpiScaleX;
        var position = PopupPlacement.Calculate(new PopupPoint(cursor.X, cursor.Y), size, work, offset);
        SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(position.X), (int)Math.Round(position.Y), 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>框选翻译（#57）：放在选区旁（右下外侧 → 下 → 上 → 左），夹紧到选区所在显示器的工作区。</summary>
    private void PlaceAroundRect(PopupRect selection)
    {
        var center = new PopupNativeMethods.Point
        {
            X = (int)Math.Round(selection.Left + (selection.Width / 2)),
            Y = (int)Math.Round(selection.Top + (selection.Height / 2)),
        };
        var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        // 先移到选区所在显示器，让 WPF 按该显示器 DPI 重新缩放，再按新尺寸计算。
        SetWindowPos(_hwnd, IntPtr.Zero, center.X, center.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = new PopupRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        ResultPanel.MaxHeight = Math.Max(80, (work.Height / dpi.DpiScaleY / 2) - 60);
        UpdateLayout();

        // 窗口四周有 ShadowMargin 的透明阴影边，卡片与选区的可见间距为 SelectionGap。
        var size = new PopupSize(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY);
        var gap = (SelectionGap - ShadowMargin) * dpi.DpiScaleX;
        var placement = PopupPlacement.CalculateAroundRect(selection, size, work, gap);
        SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(placement.Position.X), (int)Math.Round(placement.Position.Y), 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void OnSizeChanged()
    {
        // 内容变化导致尺寸变化时重新夹紧（钉住时保持用户拖到的位置）。
        if (IsVisible && !_model.IsPinned)
        {
            PlaceAtAnchor();
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_model.IsPinned && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnHover(bool hovered)
    {
        _model.SetHovered(hovered);

        // 未钉住时浮窗拿不到键盘焦点：鼠标悬停期间临时注册 Esc，移出即注销，不影响在原应用里按 Esc。
        SetEscHotkey(hovered && !_model.IsPinned);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _model.Close(PopupCloseReason.User);
            e.Handled = true;
        }
    }

    private void OpenSourceMenu()
    {
        var menu = new ContextMenu { PlacementTarget = LanguageButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "按此语种重新翻译：", IsEnabled = false });
        foreach (var language in PopupText.SourceChoices)
        {
            var item = new MenuItem { Header = language.DisplayName };
            var code = language.Code;
            item.Click += (_, _) => _model.RequestSourceOverride(code);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void SetNoActivate(bool noActivate)
    {
        var style = GetExStyle(_hwnd) | WsExToolWindow;
        style &= ~WsExAppWindow;
        style = noActivate ? style | WsExNoActivate : style & ~WsExNoActivate;
        SetExStyle(_hwnd, style);
    }

    private void SetEscHotkey(bool register)
    {
        if (register == _escRegistered)
        {
            return;
        }

        if (register)
        {
            // 失败（Esc 已被其他程序注册）时静默放弃，仍可点 × 关闭。
            _escRegistered = KeyboardNativeMethods.RegisterHotKey(_hwnd, EscHotkeyId, 0, VkEscape);
        }
        else
        {
            KeyboardNativeMethods.UnregisterHotKey(_hwnd, EscHotkeyId);
            _escRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == KeyboardNativeMethods.WmHotkey && wParam.ToInt32() == EscHotkeyId)
        {
            _model.Close(PopupCloseReason.User);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
