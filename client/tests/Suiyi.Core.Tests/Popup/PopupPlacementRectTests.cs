using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Popup;

/// <summary>以选区为锚点的放置（#57）：右下外侧 → 下 → 上 → 左 → 压住选区，最后夹紧到工作区。</summary>
public sealed class PopupPlacementRectTests
{
    // 1920×1080 主显示器，任务栏在底部占 40 px。
    private static readonly PopupRect Primary = new(0, 0, 1920, 1040);
    private static readonly PopupSize Window = new(400, 200);

    private static RectPlacement Place(PopupRect selection, PopupSize? size = null, PopupRect? work = null, double gap = 8) =>
        PopupPlacement.CalculateAroundRect(selection, size ?? Window, work ?? Primary, gap);

    [Fact]
    public void Default_OutsideBottomRightCorner()
    {
        Assert.Equal(new RectPlacement(new PopupPoint(408, 308), RectPlacementSide.BottomRight), Place(new PopupRect(100, 100, 300, 200)));
    }

    [Fact]
    public void LeftEdge_StillBottomRight()
    {
        Assert.Equal(new RectPlacement(new PopupPoint(308, 708), RectPlacementSide.BottomRight), Place(new PopupRect(0, 400, 300, 300)));
    }

    [Fact]
    public void RightEdge_FallsBackBelow_ShiftedLeft()
    {
        // 右侧放不下 → 下方；与选区左边对齐会超出右边，横向夹紧。
        Assert.Equal(new RectPlacement(new PopupPoint(1520, 308), RectPlacementSide.Below), Place(new PopupRect(1600, 100, 300, 200)));
    }

    [Fact]
    public void Bottom_FallsBackAbove()
    {
        Assert.Equal(new RectPlacement(new PopupPoint(100, 692), RectPlacementSide.Above), Place(new PopupRect(100, 900, 300, 100)));
    }

    [Fact]
    public void BottomRightCorner_FallsBackAbove()
    {
        Assert.Equal(new RectPlacement(new PopupPoint(1500, 612), RectPlacementSide.Above), Place(new PopupRect(1500, 820, 380, 200)));
    }

    [Fact]
    public void TallSelectionOnRight_FallsBackLeft()
    {
        // 选区几乎占满右侧整个高度：右、下、上都放不下 → 左侧，与选区上边对齐。
        Assert.Equal(new RectPlacement(new PopupPoint(592, 100), RectPlacementSide.Left), Place(new PopupRect(1000, 100, 900, 900)));
    }

    [Fact]
    public void LeftSide_AlignsTopButClampsVertically()
    {
        // 浮窗 1000 高：右、下、上都放不下 → 左侧；与选区上边（300）对齐会超出底边，纵向夹紧到 40。
        Assert.Equal(new RectPlacement(new PopupPoint(592, 40), RectPlacementSide.Left), Place(new PopupRect(1000, 300, 900, 200), new PopupSize(400, 1000)));
    }

    [Fact]
    public void FullScreenSelection_Overlaps_PicksLargerSpace()
    {
        // 选区覆盖整个工作区：四个方向都放不下，压在选区上并夹紧。
        var result = Place(new PopupRect(0, 0, 1920, 1040));

        Assert.Equal(RectPlacementSide.Overlap, result.Side);
        Assert.Equal(new PopupPoint(0, 840), result.Position);
    }

    [Fact]
    public void Overlap_PrefersAboveWhenMoreSpace()
    {
        // 选区占满宽度、位于底部：下方 0 px、上方 600 px，但浮窗 700 高哪里都放不下 → 上方一侧夹紧。
        var result = Place(new PopupRect(0, 600, 1920, 440), new PopupSize(400, 700));

        Assert.Equal(RectPlacementSide.Overlap, result.Side);
        Assert.Equal(new PopupPoint(0, 0), result.Position);
    }

    [Fact]
    public void WindowTallerThanWorkArea_Overlap_TopClamped()
    {
        var result = Place(new PopupRect(100, 100, 300, 200), new PopupSize(400, 1200));

        Assert.Equal(RectPlacementSide.Overlap, result.Side);
        Assert.Equal(new PopupPoint(100, 0), result.Position);
    }

    [Fact]
    public void WindowWiderThanWorkArea_LeftClamped()
    {
        var result = Place(new PopupRect(100, 100, 300, 200), new PopupSize(2000, 200));

        Assert.Equal(new RectPlacement(new PopupPoint(0, 308), RectPlacementSide.Below), result);
    }

    [Fact]
    public void HighDpiSecondary_FallsBackAbove_ClampedToItsWorkArea()
    {
        // 副屏 2560×1440 @150%，在主屏右侧，工作区 1400 高；浮窗与间距都已按 1.5 倍换成物理像素。
        var work = new PopupRect(1920, 0, 2560, 1400);
        var result = Place(new PopupRect(4000, 1100, 400, 200), new PopupSize(600, 300), work, gap: 12);

        Assert.Equal(new RectPlacement(new PopupPoint(3880, 788), RectPlacementSide.Above), result);
    }

    [Fact]
    public void NegativeCoordinateMonitor_Below()
    {
        var work = new PopupRect(-2560, -180, 2560, 1400);
        var result = Place(new PopupRect(-500, -100, 300, 100), work: work);

        Assert.Equal(new RectPlacement(new PopupPoint(-500, 8), RectPlacementSide.Below), result);
    }

    [Fact]
    public void NegativeGap_WindowShadowOverlapsSelectionEdge()
    {
        Assert.Equal(new PopupPoint(396, 296), Place(new PopupRect(100, 100, 300, 200), gap: -4).Position);
    }

    [Theory]
    [InlineData(0, 0, 50, 50)]
    [InlineData(1870, 990, 50, 50)]
    [InlineData(0, 990, 1920, 50)]
    [InlineData(900, 0, 100, 1040)]
    [InlineData(-100, -100, 50, 50)] // 选区在工作区外（不该发生）也要夹紧
    public void Result_AlwaysInsideWorkArea(double x, double y, double w, double h)
    {
        var p = Place(new PopupRect(x, y, w, h)).Position;

        Assert.InRange(p.X, Primary.Left, Primary.Right - Window.Width);
        Assert.InRange(p.Y, Primary.Top, Primary.Bottom - Window.Height);
    }
}
