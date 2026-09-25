using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Suiyi.Core.Capture;
using static Suiyi.App.Interop.ScreenCaptureNativeMethods;

namespace Suiyi.App.Capture;

/// <summary>
/// 单个显示器上的框选遮罩：铺满该显示器，显示冻结帧 + 半透明暗色，选区内还原亮度并标注尺寸（物理像素）。
/// 窗口按物理像素用 <c>SetWindowPos</c> 定位（混合 DPI 下不经 WPF 的 DIP 换算），DPI 变化后再定位一次。
/// 鼠标位置一律取 <c>GetCursorPos</c>（物理像素），交给 <see cref="RegionSelection"/>。
/// </summary>
internal sealed class RegionOverlayWindow : Window
{
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)));
    private static readonly Brush BorderBrushValue = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly Brush LabelBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)));

    private readonly DisplayMonitor _monitor;
    private readonly RegionSelection _selection;
    private readonly IntPtr _hwnd;
    private readonly Path _dim;
    private readonly Rectangle _border;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private bool _allowClose;

    public RegionOverlayWindow(DisplayMonitor monitor, BitmapSource frame, RegionSelection selection)
    {
        _monitor = monitor;
        _selection = selection;

        Title = "随译框选";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Focusable = true;

        var image = new Image { Source = frame, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        _dim = new Path { Fill = DimBrush, IsHitTestVisible = false };
        _border = new Rectangle { Stroke = BorderBrushValue, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _labelText = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI") };
        _label = new Border
        {
            Background = LabelBackground,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Child = _labelText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        var canvas = new Canvas { ClipToBounds = true };
        canvas.Children.Add(_dim);
        canvas.Children.Add(_border);
        canvas.Children.Add(_label);
        var root = new Grid();
        root.Children.Add(image);
        root.Children.Add(canvas);
        Content = root;

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        PlaceOnMonitor();
        DpiChanged += (_, _) =>
        {
            // 从主屏 DPI 移到副屏 DPI 时 WPF 会按比例改窗口大小，这里改回正好铺满显示器。
            PlaceOnMonitor();
            Render();
        };
        SizeChanged += (_, _) => Render();
        Loaded += (_, _) => PlaceOnMonitor();

        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            CancelRequested?.Invoke(this, EventArgs.Empty);
        };
        LostMouseCapture += (_, _) =>
        {
            // 拖拽中被系统夺走鼠标捕获（Alt+Tab、UAC 等）按取消处理；正常松开时状态已结束，不受影响。
            if (_selection.State == RegionSelectionState.Dragging && ReferenceEquals(_selection.Monitor, _monitor))
            {
                CancelRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _selection.Changed += OnSelectionChanged;
        Render();
    }

    /// <summary>Esc、右键或失去鼠标捕获。</summary>
    public event EventHandler? CancelRequested;

    /// <summary>左键松开（选区状态已更新）。</summary>
    public event EventHandler? Released;

    public DisplayMonitor Monitor => _monitor;

    public IntPtr Handle => _hwnd;

    /// <summary>框选结束时关闭（普通 Alt+F4 按取消处理）。</summary>
    public void CloseOverlay()
    {
        _allowClose = true;
        _selection.Changed -= OnSelectionChanged;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    private static PixelPoint CursorPosition() =>
        Interop.PopupNativeMethods.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void PlaceOnMonitor()
    {
        var b = _monitor.Bounds;
        Interop.PopupNativeMethods.SetWindowPos(_hwnd, HwndTopmost, b.X, b.Y, b.Width, b.Height, SwpNoActivate);
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_selection.Begin(CursorPosition()))
        {
            CaptureMouse();
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_selection.State == RegionSelectionState.Dragging)
        {
            _selection.Move(CursorPosition());
        }
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_selection.State != RegionSelectionState.Dragging)
        {
            return;
        }

        _selection.End(CursorPosition());
        ReleaseMouseCapture();
        Released?.Invoke(this, EventArgs.Empty);
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : _monitor.Scale;
        var full = new Rect(0, 0, _monitor.Bounds.Width / scale, _monitor.Bounds.Height / scale);
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(full));

        var mine = ReferenceEquals(_selection.Monitor, _monitor) && !_selection.Rect.IsEmpty;
        if (mine)
        {
            var origin = new PixelPoint(_monitor.Bounds.X, _monitor.Bounds.Y);
            var r = ScreenCoordinates.PhysicalToLocalDip(_selection.Rect, origin, scale);
            var rect = new Rect(r.X, r.Y, r.Width, r.Height);
            geometry.Children.Add(new RectangleGeometry(rect));

            // 边框画在选区外侧 1 物理像素，不遮住选中的内容。
            var px = 1 / scale;
            _border.StrokeThickness = px;
            _border.Width = rect.Width + (2 * px);
            _border.Height = rect.Height + (2 * px);
            Canvas.SetLeft(_border, rect.X - px);
            Canvas.SetTop(_border, rect.Y - px);
            _border.Visibility = Visibility.Visible;

            _labelText.Text = string.Create(CultureInfo.InvariantCulture, $"{_selection.Rect.Width} × {_selection.Rect.Height}");
            _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelHeight = _label.DesiredSize.Height;
            var top = rect.Y - labelHeight - 4;
            Canvas.SetLeft(_label, Math.Min(rect.X, Math.Max(0, full.Width - _label.DesiredSize.Width)));
            Canvas.SetTop(_label, top >= 0 ? top : rect.Y + 4);
            _label.Visibility = Visibility.Visible;
        }
        else
        {
            _border.Visibility = Visibility.Collapsed;
            _label.Visibility = Visibility.Collapsed;
        }

        geometry.Freeze();
        _dim.Data = geometry;
    }
}
