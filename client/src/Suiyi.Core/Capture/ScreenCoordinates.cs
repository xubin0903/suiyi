namespace Suiyi.Core.Capture;

/// <summary>
/// 多显示器坐标换算（纯函数）。约定：
/// <list type="bullet">
/// <item>屏幕坐标一律是物理像素的虚拟桌面坐标（Per-Monitor V2 进程里 <c>GetCursorPos</c>、<c>GetMonitorInfo</c>、<c>BitBlt</c> 用的都是它）。</item>
/// <item>每个显示器一个遮罩窗口，窗口正好铺满该显示器；窗口内坐标是 DIP，原点为显示器左上角：物理 = 显示器原点 + DIP × 缩放。</item>
/// </list>
/// </summary>
public static class ScreenCoordinates
{
    /// <summary>所有显示器的外接矩形（虚拟桌面）。没有显示器时为空矩形。</summary>
    public static PixelRect VirtualBounds(IReadOnlyList<DisplayMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            return default;
        }

        var bounds = monitors[0].Bounds;
        for (var i = 1; i < monitors.Count; i++)
        {
            bounds = bounds.Union(monitors[i].Bounds);
        }

        return bounds;
    }

    /// <summary>点所在显示器；不在任何显示器上（显示器之间的空隙）时取最近的一台。没有显示器时返回 <see langword="null"/>。</summary>
    public static DisplayMonitor? FindMonitor(IReadOnlyList<DisplayMonitor> monitors, PixelPoint point)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        DisplayMonitor? nearest = null;
        var best = long.MaxValue;
        foreach (var monitor in monitors)
        {
            if (monitor.Bounds.Contains(point))
            {
                return monitor;
            }

            var clamped = monitor.Bounds.Clamp(point);
            long dx = clamped.X - point.X;
            long dy = clamped.Y - point.Y;
            var distance = (dx * dx) + (dy * dy);
            if (distance < best)
            {
                best = distance;
                nearest = monitor;
            }
        }

        return nearest;
    }

    /// <summary>屏幕物理像素点 → 窗口内 DIP（窗口铺满 <paramref name="origin"/> 所在显示器，缩放 <paramref name="scale"/>）。</summary>
    public static DipPoint PhysicalToLocalDip(PixelPoint point, PixelPoint origin, double scale)
    {
        ValidateScale(scale);
        return new DipPoint((point.X - origin.X) / scale, (point.Y - origin.Y) / scale);
    }

    /// <summary>屏幕物理像素点 → <paramref name="monitor"/> 上遮罩窗口内的 DIP。</summary>
    public static DipPoint PhysicalToLocalDip(PixelPoint point, DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return PhysicalToLocalDip(point, new PixelPoint(monitor.Bounds.X, monitor.Bounds.Y), monitor.Scale);
    }

    /// <summary>屏幕物理像素矩形 → 窗口内 DIP 矩形。</summary>
    public static DipRect PhysicalToLocalDip(PixelRect rect, PixelPoint origin, double scale)
    {
        var topLeft = PhysicalToLocalDip(new PixelPoint(rect.X, rect.Y), origin, scale);
        return new DipRect(topLeft.X, topLeft.Y, rect.Width / scale, rect.Height / scale);
    }

    /// <summary>屏幕物理像素矩形 → <paramref name="monitor"/> 上遮罩窗口内的 DIP 矩形。</summary>
    public static DipRect PhysicalToLocalDip(PixelRect rect, DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return PhysicalToLocalDip(rect, new PixelPoint(monitor.Bounds.X, monitor.Bounds.Y), monitor.Scale);
    }

    /// <summary>窗口内 DIP → 屏幕物理像素（四舍五入到整像素）。</summary>
    public static PixelPoint LocalDipToPhysical(DipPoint point, DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var scale = monitor.Scale;
        return new PixelPoint(
            monitor.Bounds.X + (int)Math.Round(point.X * scale, MidpointRounding.AwayFromZero),
            monitor.Bounds.Y + (int)Math.Round(point.Y * scale, MidpointRounding.AwayFromZero));
    }

    /// <summary>显示器物理尺寸对应的窗口 DIP 尺寸（例如 150% 下 2880×1620 → 1920×1080）。</summary>
    public static DipRect MonitorDipSize(DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return new DipRect(0, 0, monitor.Bounds.Width / monitor.Scale, monitor.Bounds.Height / monitor.Scale);
    }

    /// <summary>
    /// 选区在冻结帧位图里的像素区域：<paramref name="frameBounds"/> 是位图覆盖的屏幕矩形（位图左上角 = 该矩形左上角）。
    /// 选区超出位图的部分被裁掉。
    /// </summary>
    public static PixelRect ToFrameRegion(PixelRect selection, PixelRect frameBounds)
    {
        var inside = selection.Intersect(frameBounds);
        return inside.IsEmpty ? default : inside.Offset(-frameBounds.X, -frameBounds.Y);
    }

    private static void ValidateScale(double scale)
    {
        if (!(scale > 0) || double.IsInfinity(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "缩放系数必须为正数");
        }
    }
}
