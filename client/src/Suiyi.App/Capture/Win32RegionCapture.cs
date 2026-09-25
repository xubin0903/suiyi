using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Suiyi.App.Interop;
using Suiyi.Core.Capture;
using Suiyi.Core.Logging;
using static Suiyi.App.Interop.ScreenCaptureNativeMethods;

namespace Suiyi.App.Capture;

/// <summary>
/// <see cref="IRegionCapture"/> 的 Windows 实现：
/// 隐藏随译自身浮窗 → GDI <c>BitBlt</c> 逐个显示器抓「冻结帧」→ 每个显示器一个遮罩窗口 → 用户拖拽 →
/// 关闭遮罩 → 从起始显示器的冻结帧裁出选区并编码 PNG（只在内存中）。
/// 选区跨显示器时裁剪到起始显示器（不拼接）。必须在 UI 线程调用。
/// </summary>
internal sealed class Win32RegionCapture : IRegionCapture
{
    private const int EscHotkeyId = 0x5302;

    private readonly Func<bool> _hideOwnWindows;
    private readonly Action _restoreOwnWindows;
    private readonly IAppLogger _logger;

    /// <param name="hideOwnWindows">抓屏前隐藏随译自己的窗口（浮窗），返回是否隐藏了东西。</param>
    /// <param name="restoreOwnWindows">框选结束后恢复。</param>
    /// <param name="logger">日志。</param>
    public Win32RegionCapture(Func<bool> hideOwnWindows, Action restoreOwnWindows, IAppLogger? logger = null)
    {
        _hideOwnWindows = hideOwnWindows;
        _restoreOwnWindows = restoreOwnWindows;
        _logger = logger ?? NullAppLogger.Instance;
    }

    public async Task<RegionCaptureResult?> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hid = _hideOwnWindows();
        try
        {
            if (hid)
            {
                // 等 DWM 合成完隐藏后的画面，否则 BitBlt 可能还抓到浮窗。
                _ = DwmFlush();
            }

            var monitors = MonitorEnumerator.GetMonitors();
            if (monitors.Count == 0)
            {
                _logger.Warn("框选：没有枚举到显示器");
                return null;
            }

            var frames = new Dictionary<DisplayMonitor, BitmapSource>();
            foreach (var monitor in monitors)
            {
                frames[monitor] = GdiScreenGrabber.Capture(monitor.Bounds);
            }

            var selection = await SelectAsync(monitors, frames, cancellationToken).ConfigureAwait(true);
            if (selection is null)
            {
                return null;
            }

            var (rect, owner) = selection.Value;
            var frame = frames[owner];
            var region = ScreenCoordinates.ToFrameRegion(rect, owner.Bounds);
            var png = await Task.Run(() => GdiScreenGrabber.EncodePng(frame, region), CancellationToken.None).ConfigureAwait(true);
            return new RegionCaptureResult(png, rect, owner);
        }
        finally
        {
            if (hid)
            {
                _restoreOwnWindows();
            }
        }
    }

    private static PixelPoint CursorPosition() =>
        PopupNativeMethods.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;

    private async Task<(PixelRect Rect, DisplayMonitor Monitor)?> SelectAsync(
        IReadOnlyList<DisplayMonitor> monitors,
        Dictionary<DisplayMonitor, BitmapSource> frames,
        CancellationToken cancellationToken)
    {
        var selection = new RegionSelection(monitors);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new List<RegionOverlayWindow>(monitors.Count);
        HwndSource? escSource = null;
        HwndSourceHook? escHook = null;
        var escWindow = IntPtr.Zero;

        void Finish()
        {
            if (!selection.IsFinished)
            {
                selection.Cancel();
            }

            done.TrySetResult(true);
        }

        void OnCancel(object? sender, EventArgs e) => Finish();

        void OnDeactivated(object? sender, EventArgs e)
        {
            // 切到其他程序（Alt+Tab、Win 键）时取消，避免遮罩残留在后台。
            if (!selection.IsFinished)
            {
                _logger.Info("框选：随译失去焦点，取消");
                Finish();
            }
        }

        try
        {
            foreach (var monitor in monitors)
            {
                var window = new RegionOverlayWindow(monitor, frames[monitor], selection);
                window.CancelRequested += OnCancel;
                window.Released += OnCancel;
                windows.Add(window);
            }

            foreach (var window in windows)
            {
                window.Show();
            }

            // 激活光标所在显示器上的遮罩，让它收到 Esc。快捷键回调中本进程有前台权限。
            var cursorMonitor = ScreenCoordinates.FindMonitor(monitors, CursorPosition());
            var active = windows.Find(w => ReferenceEquals(w.Monitor, cursorMonitor)) ?? windows[0];
            active.Activate();
            SetForegroundWindow(active.Handle);
            active.Focus();

            // 兜底：遮罩没拿到前台时 Esc 也能取消（注册失败则只靠键盘焦点与右键）。
            escSource = HwndSource.FromHwnd(active.Handle);
            if (escSource is not null)
            {
                escHook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == KeyboardNativeMethods.WmHotkey && wParam.ToInt32() == EscHotkeyId)
                    {
                        handled = true;
                        Finish();
                    }

                    return IntPtr.Zero;
                };
                escSource.AddHook(escHook);
                if (KeyboardNativeMethods.RegisterHotKey(active.Handle, EscHotkeyId, 0, PopupNativeMethods.VkEscape))
                {
                    escWindow = active.Handle;
                }
            }

            Application.Current.Deactivated += OnDeactivated;
            using (cancellationToken.Register(Finish))
            {
                await done.Task.ConfigureAwait(true);
            }
        }
        finally
        {
            if (Application.Current is { } app)
            {
                app.Deactivated -= OnDeactivated;
            }

            if (escWindow != IntPtr.Zero)
            {
                KeyboardNativeMethods.UnregisterHotKey(escWindow, EscHotkeyId);
            }

            if (escSource is not null && escHook is not null)
            {
                escSource.RemoveHook(escHook);
            }

            foreach (var window in windows)
            {
                window.CancelRequested -= OnCancel;
                window.Released -= OnCancel;
                window.CloseOverlay();
            }

            // 遮罩关闭后等 DWM 合成一帧，之后恢复的浮窗和下一次截图都不会叠上遮罩残影。
            _ = DwmFlush();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return selection.State == RegionSelectionState.Completed ? (selection.Rect, selection.Monitor!) : null;
    }
}
