using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Popup;

public sealed class PopupPlacementTests
{
    // 1920×1080 主显示器，任务栏在底部占 40 px。
    private static readonly PopupRect Primary = new(0, 0, 1920, 1040);
    private static readonly PopupSize Window = new(400, 200);

    private static PopupPoint Place(double x, double y, PopupSize? size = null, PopupRect? work = null, double offset = 16) =>
        PopupPlacement.Calculate(new PopupPoint(x, y), size ?? Window, work ?? Primary, offset);

    [Fact]
    public void Default_BottomRightOfCursor()
    {
        Assert.Equal(new PopupPoint(516, 316), Place(500, 300));
    }

    [Fact]
    public void TopLeftCorner_StaysBottomRight()
    {
        Assert.Equal(new PopupPoint(16, 16), Place(0, 0));
    }

    [Fact]
    public void BottomRightCorner_FlipsLeftAndUp()
    {
        Assert.Equal(new PopupPoint(1919 - 16 - 400, 1039 - 16 - 200), Place(1919, 1039));
    }

    [Fact]
    public void RightEdge_FlipsLeftOnly()
    {
        Assert.Equal(new PopupPoint(1800 - 16 - 400, 316), Place(1800, 300));
    }

    [Fact]
    public void BottomEdge_FlipsUpOnly()
    {
        Assert.Equal(new PopupPoint(516, 1000 - 16 - 200), Place(500, 1000));
    }

    [Fact]
    public void ExactFit_DoesNotFlip()
    {
        // 右边正好贴到工作区右边界。
        Assert.Equal(1520, Place(1504, 300).X);
    }

    [Fact]
    public void NeitherSideFits_ClampedInsideWorkArea()
    {
        // 窄工作区：左右都放不下时翻到左侧后被夹到左边界，窗口完整留在工作区内。
        var work = new PopupRect(0, 0, 600, 1040);
        Assert.Equal(0, Place(300, 100, work: work).X);
        Assert.Equal(new PopupRect(100, 0, 500, 1040).Left, Place(300, 100, work: new PopupRect(100, 0, 500, 1040)).X);
    }

    [Fact]
    public void LargerThanWorkArea_PinnedToTopLeft()
    {
        var p = Place(500, 500, size: new PopupSize(3000, 2000));

        Assert.Equal(new PopupPoint(0, 0), p);
    }

    [Fact]
    public void SecondaryMonitorLeftOfPrimary_NegativeCoordinates()
    {
        // 主显示器左侧的 1280×1024 副屏，工作区 x ∈ [-1280, 0)。
        var left = new PopupRect(-1280, 0, 1280, 1024);

        Assert.Equal(new PopupPoint(-1000 + 16, 316), Place(-1000, 300, work: left));
        Assert.Equal(new PopupPoint(-10 - 16 - 400, 316), Place(-10, 300, work: left));
    }

    [Fact]
    public void SecondaryMonitorAbovePrimary_NegativeY()
    {
        var above = new PopupRect(0, -1080, 1920, 1080);

        Assert.Equal(new PopupPoint(516, -20 - 16 - 200), Place(500, -20, work: above));
        Assert.Equal(new PopupPoint(516, -1080 + 16), Place(500, -1080, work: above));
    }

    [Fact]
    public void WorkAreaWithTopTaskbar_NeverAboveWorkArea()
    {
        var work = new PopupRect(0, 40, 1920, 1040);

        Assert.Equal(40, Place(500, 1070, size: new PopupSize(400, 1030), work: work).Y);
    }

    [Fact]
    public void ZeroOffset_Allowed()
    {
        Assert.Equal(new PopupPoint(500, 300), Place(500, 300, offset: 0));
    }

    [Fact]
    public void Rect_RightAndBottom()
    {
        var r = new PopupRect(-100, -50, 300, 200);

        Assert.Equal(200, r.Right);
        Assert.Equal(150, r.Bottom);
    }
}
