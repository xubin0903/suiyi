using Suiyi.Core.Capture;

namespace Suiyi.Core.Tests.Capture;

public sealed class PixelRectTests
{
    [Fact]
    public void FromCorners_ForwardDrag_IncludesBothEndPixels()
    {
        Assert.Equal(new PixelRect(10, 20, 91, 81), PixelRect.FromCorners(new PixelPoint(10, 20), new PixelPoint(100, 100)));
    }

    [Theory]
    [InlineData(100, 100, 50, 60)] // 右下 → 左上
    [InlineData(50, 100, 100, 60)] // 左下 → 右上
    [InlineData(100, 60, 50, 100)] // 右上 → 左下
    [InlineData(50, 60, 100, 100)] // 左上 → 右下
    public void FromCorners_AnyDirection_SameRect(int ax, int ay, int bx, int by)
    {
        Assert.Equal(new PixelRect(50, 60, 51, 41), PixelRect.FromCorners(new PixelPoint(ax, ay), new PixelPoint(bx, by)));
    }

    [Fact]
    public void FromCorners_NegativeCoordinates()
    {
        Assert.Equal(new PixelRect(-500, -300, 401, 201), PixelRect.FromCorners(new PixelPoint(-100, -100), new PixelPoint(-500, -300)));
    }

    [Fact]
    public void FromCorners_SamePoint_OnePixel()
    {
        Assert.Equal(new PixelRect(5, 5, 1, 1), PixelRect.FromCorners(new PixelPoint(5, 5), new PixelPoint(5, 5)));
    }

    [Fact]
    public void Edges_RightBottomExclusive()
    {
        var r = new PixelRect(-10, -20, 30, 40);

        Assert.Equal(20, r.Right);
        Assert.Equal(20, r.Bottom);
        Assert.True(r.Contains(new PixelPoint(-10, -20)));
        Assert.True(r.Contains(new PixelPoint(19, 19)));
        Assert.False(r.Contains(new PixelPoint(20, 19)));
        Assert.False(r.Contains(new PixelPoint(19, 20)));
    }

    [Fact]
    public void FromEdges_Inverted_Empty()
    {
        var r = PixelRect.FromEdges(10, 10, 5, 20);

        Assert.True(r.IsEmpty);
        Assert.Equal(0, r.Width);
    }

    [Fact]
    public void Intersect_And_Union()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, -50, 100, 100);

        Assert.Equal(new PixelRect(50, 0, 50, 50), a.Intersect(b));
        Assert.Equal(new PixelRect(0, -50, 150, 150), a.Union(b));
        Assert.True(a.Intersect(new PixelRect(200, 200, 10, 10)).IsEmpty);
    }

    [Fact]
    public void Contains_Rect()
    {
        var a = new PixelRect(0, 0, 100, 100);

        Assert.True(a.Contains(new PixelRect(0, 0, 100, 100)));
        Assert.True(a.Contains(new PixelRect(10, 10, 5, 5)));
        Assert.False(a.Contains(new PixelRect(90, 90, 11, 5)));
    }

    [Fact]
    public void Clamp_KeepsInsideLastPixel()
    {
        var r = new PixelRect(-1920, 0, 1920, 1080);

        Assert.Equal(new PixelPoint(-1, 1079), r.Clamp(new PixelPoint(500, 5000)));
        Assert.Equal(new PixelPoint(-1920, 0), r.Clamp(new PixelPoint(-5000, -5)));
        Assert.Equal(new PixelPoint(-100, 50), r.Clamp(new PixelPoint(-100, 50)));
        Assert.Equal(new PixelPoint(3, 4), new PixelRect(3, 4, 0, 0).Clamp(new PixelPoint(100, 100)));
    }

    [Fact]
    public void ToString_Invariant()
    {
        Assert.Equal("(-1920,0) 1920×1080", new PixelRect(-1920, 0, 1920, 1080).ToString());
    }

    [Fact]
    public void Monitor_Scale()
    {
        Assert.Equal(1.0, Monitors.Primary100.Scale);
        Assert.Equal(1.25, Monitors.Left125.Scale);
        Assert.Equal(1.5, Monitors.Right150.Scale);
        Assert.Equal(2.0, Monitors.Top200.Scale);
        Assert.Equal(1.0, new DisplayMonitor("x", new PixelRect(0, 0, 1, 1), 0).Scale);
    }
}
