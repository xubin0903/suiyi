using Suiyi.Core.Capture;

namespace Suiyi.Core.Tests.Capture;

/// <summary>测试用显示器布局（物理像素）。</summary>
internal static class Monitors
{
    /// <summary>1920×1080，100%。</summary>
    public static DisplayMonitor Primary100 { get; } = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), 96, IsPrimary: true);

    /// <summary>2880×1620，150%（相当于 1920×1080 DIP）。</summary>
    public static DisplayMonitor Primary150 { get; } = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 2880, 1620), 144, IsPrimary: true);

    /// <summary>副屏 2560×1440 150%，在主屏右侧，顶端对齐。</summary>
    public static DisplayMonitor Right150 { get; } = new(@"\\.\DISPLAY2", new PixelRect(1920, 0, 2560, 1440), 144);

    /// <summary>副屏 2560×1440 125%，在主屏左侧（负坐标），下沿比主屏低 180。</summary>
    public static DisplayMonitor Left125 { get; } = new(@"\\.\DISPLAY3", new PixelRect(-2560, -180, 2560, 1440), 120);

    /// <summary>副屏 3840×2160 200%，在主屏上方（负 Y）。</summary>
    public static DisplayMonitor Top200 { get; } = new(@"\\.\DISPLAY4", new PixelRect(-960, -2160, 3840, 2160), 192);
}
