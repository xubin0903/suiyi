using Suiyi.Core.Capture;

namespace Suiyi.Core.Tests.Capture;

/// <summary>坐标换算：单屏 100% / 150%、双屏不同缩放、副屏负坐标、反向拖拽（#55 验收）。</summary>
public sealed class ScreenCoordinatesTests
{
    private static readonly DisplayMonitor[] DualMixed = [Monitors.Primary100, Monitors.Right150];
    private static readonly DisplayMonitor[] LeftNegative = [Monitors.Primary100, Monitors.Left125];

    // ---- 单屏 100% ----

    [Fact]
    public void Single100_PhysicalEqualsDip()
    {
        var m = Monitors.Primary100;

        Assert.Equal(new DipPoint(300, 200), ScreenCoordinates.PhysicalToLocalDip(new PixelPoint(300, 200), m));
        Assert.Equal(new PixelPoint(300, 200), ScreenCoordinates.LocalDipToPhysical(new DipPoint(300, 200), m));
        Assert.Equal(new DipRect(10, 20, 640, 480), ScreenCoordinates.PhysicalToLocalDip(new PixelRect(10, 20, 640, 480), m));
        Assert.Equal(new DipRect(0, 0, 1920, 1080), ScreenCoordinates.MonitorDipSize(m));
    }

    // ---- 单屏 150% ----

    [Fact]
    public void Single150_DividesByScale()
    {
        var m = Monitors.Primary150;

        Assert.Equal(new DipPoint(200, 100), ScreenCoordinates.PhysicalToLocalDip(new PixelPoint(300, 150), m));
        Assert.Equal(new DipRect(100, 200, 400, 300), ScreenCoordinates.PhysicalToLocalDip(new PixelRect(150, 300, 600, 450), m));
        Assert.Equal(new DipRect(0, 0, 1920, 1080), ScreenCoordinates.MonitorDipSize(m));
    }

    [Fact]
    public void Single150_DipToPhysical_Rounds()
    {
        var m = Monitors.Primary150;

        Assert.Equal(new PixelPoint(300, 150), ScreenCoordinates.LocalDipToPhysical(new DipPoint(200, 100), m));

        // 1 DIP = 1.5 px：0.5 DIP → 0.75 → 1；1 DIP → 1.5 → 2（远离零舍入）。
        Assert.Equal(new PixelPoint(1, 2), ScreenCoordinates.LocalDipToPhysical(new DipPoint(0.5, 1), m));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2879, 1619)]
    [InlineData(1234, 567)]
    public void Single150_RoundTrip_PixelExact(int x, int y)
    {
        var m = Monitors.Primary150;
        var dip = ScreenCoordinates.PhysicalToLocalDip(new PixelPoint(x, y), m);

        Assert.Equal(new PixelPoint(x, y), ScreenCoordinates.LocalDipToPhysical(dip, m));
    }

    // ---- 双屏不同缩放 ----

    [Fact]
    public void DualMixed_VirtualBounds()
    {
        Assert.Equal(new PixelRect(0, 0, 4480, 1440), ScreenCoordinates.VirtualBounds(DualMixed));
    }

    [Theory]
    [InlineData(0, 0, @"\\.\DISPLAY1")]
    [InlineData(1919, 1079, @"\\.\DISPLAY1")]
    [InlineData(1920, 0, @"\\.\DISPLAY2")]
    [InlineData(4479, 1439, @"\\.\DISPLAY2")]
    public void DualMixed_FindMonitor(int x, int y, string device)
    {
        Assert.Equal(device, ScreenCoordinates.FindMonitor(DualMixed, new PixelPoint(x, y))!.DeviceName);
    }

    [Fact]
    public void DualMixed_GapBelowPrimary_NearestMonitor()
    {
        // 主屏只有 1080 高，副屏 1440 高：主屏下方 (100, 1300) 不在任何显示器上，最近的是主屏。
        Assert.Equal(@"\\.\DISPLAY1", ScreenCoordinates.FindMonitor(DualMixed, new PixelPoint(100, 1300))!.DeviceName);

        // 靠近副屏左下的空隙 (1900, 1400) 离副屏更近。
        Assert.Equal(@"\\.\DISPLAY2", ScreenCoordinates.FindMonitor(DualMixed, new PixelPoint(1900, 1400))!.DeviceName);
    }

    [Fact]
    public void DualMixed_SecondaryUsesOwnOriginAndScale()
    {
        var m = Monitors.Right150;

        // 副屏物理 (1920+300, 150) → 副屏窗口内 (200, 100) DIP；与主屏的 100% 无关。
        Assert.Equal(new DipPoint(200, 100), ScreenCoordinates.PhysicalToLocalDip(new PixelPoint(2220, 150), m));
        Assert.Equal(new PixelPoint(2220, 150), ScreenCoordinates.LocalDipToPhysical(new DipPoint(200, 100), m));
        Assert.Equal(new DipRect(0, 0, 2560 / 1.5, 960), ScreenCoordinates.MonitorDipSize(m));
    }

    [Fact]
    public void DualMixed_SameDipDifferentPhysicalSize()
    {
        var onPrimary = ScreenCoordinates.PhysicalToLocalDip(new PixelRect(100, 100, 300, 300), Monitors.Primary100);
        var onSecondary = ScreenCoordinates.PhysicalToLocalDip(new PixelRect(2020, 100, 300, 300), Monitors.Right150);

        Assert.Equal(300, onPrimary.Width);
        Assert.Equal(200, onSecondary.Width);
    }

    // ---- 副屏负坐标 ----

    [Fact]
    public void LeftNegative_VirtualBounds()
    {
        Assert.Equal(new PixelRect(-2560, -180, 4480, 1440), ScreenCoordinates.VirtualBounds(LeftNegative));
    }

    [Theory]
    [InlineData(-1, 0, @"\\.\DISPLAY3")]
    [InlineData(-2560, -180, @"\\.\DISPLAY3")]
    [InlineData(0, 0, @"\\.\DISPLAY1")]
    [InlineData(-100, 1200, @"\\.\DISPLAY3")]
    public void LeftNegative_FindMonitor(int x, int y, string device)
    {
        Assert.Equal(device, ScreenCoordinates.FindMonitor(LeftNegative, new PixelPoint(x, y))!.DeviceName);
    }

    [Fact]
    public void LeftNegative_LocalDip()
    {
        var m = Monitors.Left125;

        // 物理 (-2560+250, -180+125) → 窗口内 (200, 100) DIP。
        Assert.Equal(new DipPoint(200, 100), ScreenCoordinates.PhysicalToLocalDip(new PixelPoint(-2310, -55), m));
        Assert.Equal(new PixelPoint(-2310, -55), ScreenCoordinates.LocalDipToPhysical(new DipPoint(200, 100), m));
        Assert.Equal(new DipRect(200, 100, 400, 80), ScreenCoordinates.PhysicalToLocalDip(new PixelRect(-2310, -55, 500, 100), m));
    }

    [Fact]
    public void TopNegative200_LocalDip()
    {
        var m = Monitors.Top200;

        Assert.Equal(new DipPoint(480, 1080), ScreenCoordinates.PhysicalToLocalDip(new PixelPoint(0, 0), m));
        Assert.Equal(new PixelPoint(-960, -2160), ScreenCoordinates.LocalDipToPhysical(new DipPoint(0, 0), m));
        Assert.Equal(new DipRect(0, 0, 1920, 1080), ScreenCoordinates.MonitorDipSize(m));
    }

    // ---- 反向拖拽 → 换算 ----

    [Fact]
    public void ReverseDrag_OnScaledNegativeMonitor()
    {
        var m = Monitors.Left125;
        var rect = PixelRect.FromCorners(new PixelPoint(-1000, 500), new PixelPoint(-1499, 101));

        Assert.Equal(new PixelRect(-1499, 101, 500, 400), rect);
        Assert.Equal(new DipRect(848.8, 224.8, 400, 320), Round(ScreenCoordinates.PhysicalToLocalDip(rect, m)));
    }

    // ---- 冻结帧裁剪 ----

    [Fact]
    public void ToFrameRegion_OffsetsByFrameOrigin()
    {
        Assert.Equal(new PixelRect(1061, 281, 500, 400), ScreenCoordinates.ToFrameRegion(new PixelRect(-1499, 101, 500, 400), Monitors.Left125.Bounds));
        Assert.Equal(new PixelRect(100, 50, 10, 10), ScreenCoordinates.ToFrameRegion(new PixelRect(2020, 50, 10, 10), Monitors.Right150.Bounds));
    }

    [Fact]
    public void ToFrameRegion_ClipsToFrame()
    {
        Assert.Equal(new PixelRect(1910, 0, 10, 10), ScreenCoordinates.ToFrameRegion(new PixelRect(1910, -5, 20, 15), Monitors.Primary100.Bounds));
        Assert.True(ScreenCoordinates.ToFrameRegion(new PixelRect(5000, 0, 10, 10), Monitors.Primary100.Bounds).IsEmpty);
    }

    // ---- 边界 ----

    [Fact]
    public void Empty_Inputs()
    {
        Assert.True(ScreenCoordinates.VirtualBounds([]).IsEmpty);
        Assert.Null(ScreenCoordinates.FindMonitor([], new PixelPoint(0, 0)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidScale_Throws(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenCoordinates.PhysicalToLocalDip(new PixelPoint(1, 1), default, scale));
    }

    private static DipRect Round(DipRect r) => new(Math.Round(r.X, 6), Math.Round(r.Y, 6), Math.Round(r.Width, 6), Math.Round(r.Height, 6));
}
