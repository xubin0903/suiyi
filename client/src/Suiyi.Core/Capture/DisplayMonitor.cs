namespace Suiyi.Core.Capture;

/// <summary>一台显示器（Per-Monitor V2 下取到的物理像素边界与有效 DPI）。</summary>
/// <param name="DeviceName">设备名，例如 <c>\\.\DISPLAY1</c>。</param>
/// <param name="Bounds">整个显示器区域（物理像素，虚拟桌面坐标）。</param>
/// <param name="Dpi">有效 DPI：96 = 100%，144 = 150%，192 = 200%。</param>
/// <param name="IsPrimary">是否主显示器。</param>
public sealed record DisplayMonitor(string DeviceName, PixelRect Bounds, int Dpi, bool IsPrimary = false)
{
    /// <summary>100% 缩放对应的 DPI。</summary>
    public const int BaseDpi = 96;

    /// <summary>缩放系数：DIP × <see cref="Scale"/> = 物理像素。</summary>
    public double Scale => Dpi > 0 ? Dpi / (double)BaseDpi : 1.0;
}
