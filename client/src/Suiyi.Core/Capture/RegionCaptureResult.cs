namespace Suiyi.Core.Capture;

/// <summary>框选截图结果。图片只在内存中，不写磁盘。</summary>
/// <param name="Png">选区的 PNG（物理像素，1:1 未缩放）。</param>
/// <param name="Bounds">选区屏幕矩形（物理像素，虚拟桌面坐标，可为负）。</param>
/// <param name="Monitor">选区所在显示器（跨屏拖拽时为起始显示器，选区已裁剪到它内部）。</param>
public sealed record RegionCaptureResult(ReadOnlyMemory<byte> Png, PixelRect Bounds, DisplayMonitor Monitor)
{
    /// <summary>选区所在显示器的 DPI 缩放（1.0 = 100%，1.5 = 150%）。</summary>
    public double Scale => Monitor.Scale;
}
