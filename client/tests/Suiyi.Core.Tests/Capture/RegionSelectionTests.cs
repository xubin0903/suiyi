using Suiyi.Core.Capture;

namespace Suiyi.Core.Tests.Capture;

public sealed class RegionSelectionTests
{
    private static readonly DisplayMonitor[] Layout = [Monitors.Primary100, Monitors.Right150, Monitors.Left125];

    private readonly RegionSelection _selection = new(Layout);
    private int _changes;

    public RegionSelectionTests()
    {
        _selection.Changed += (_, _) => _changes++;
    }

    [Fact]
    public void Initial_Idle()
    {
        Assert.Equal(RegionSelectionState.Idle, _selection.State);
        Assert.Null(_selection.Monitor);
        Assert.True(_selection.Rect.IsEmpty);
        Assert.Equal(8, _selection.MinSize);
        Assert.False(_selection.IsFinished);
    }

    [Fact]
    public void Drag_Completes()
    {
        Assert.True(_selection.Begin(new PixelPoint(100, 100)));
        Assert.Equal(RegionSelectionState.Dragging, _selection.State);
        Assert.Same(Monitors.Primary100, _selection.Monitor);

        Assert.True(_selection.Move(new PixelPoint(300, 250)));
        Assert.Equal(new PixelRect(100, 100, 201, 151), _selection.Rect);

        Assert.Equal(RegionSelectionState.Completed, _selection.End(new PixelPoint(399, 299)));
        Assert.Equal(new PixelRect(100, 100, 300, 200), _selection.Rect);
        Assert.True(_selection.IsFinished);
        Assert.Equal(3, _changes);
    }

    [Fact]
    public void ReverseDrag_BottomRightToTopLeft()
    {
        _selection.Begin(new PixelPoint(500, 400));
        _selection.Move(new PixelPoint(450, 380));

        Assert.Equal(RegionSelectionState.Completed, _selection.End(new PixelPoint(101, 201)));
        Assert.Equal(new PixelRect(101, 201, 400, 200), _selection.Rect);
    }

    [Fact]
    public void Move_SamePoint_NoChange()
    {
        _selection.Begin(new PixelPoint(10, 10));
        _selection.Move(new PixelPoint(50, 50));
        var before = _changes;

        Assert.False(_selection.Move(new PixelPoint(50, 50)));
        Assert.Equal(before, _changes);
    }

    [Theory]
    [InlineData(107, 107, RegionSelectionState.Completed)] // 8×8 刚好够
    [InlineData(106, 107, RegionSelectionState.Cancelled)] // 7×8
    [InlineData(107, 106, RegionSelectionState.Cancelled)] // 8×7
    [InlineData(100, 100, RegionSelectionState.Cancelled)] // 单击
    [InlineData(300, 102, RegionSelectionState.Cancelled)] // 细长条
    public void MinSize_8x8(int endX, int endY, RegionSelectionState expected)
    {
        _selection.Begin(new PixelPoint(100, 100));

        Assert.Equal(expected, _selection.End(new PixelPoint(endX, endY)));
    }

    [Fact]
    public void CustomMinSize()
    {
        var selection = new RegionSelection(Layout, minSize: 1);
        selection.Begin(new PixelPoint(5, 5));

        Assert.Equal(RegionSelectionState.Completed, selection.End(new PixelPoint(5, 5)));
    }

    [Fact]
    public void CrossMonitor_ClampedToStartMonitor()
    {
        // 从主屏 (1800, 500) 拖到副屏 (2500, 1300)：裁剪到主屏右边与下边。
        _selection.Begin(new PixelPoint(1800, 500));
        _selection.Move(new PixelPoint(2500, 1300));

        Assert.Equal(new PixelRect(1800, 500, 120, 580), _selection.Rect);
        Assert.Equal(RegionSelectionState.Completed, _selection.End(new PixelPoint(2500, 1300)));
        Assert.Same(Monitors.Primary100, _selection.Monitor);
        Assert.True(Monitors.Primary100.Bounds.Contains(_selection.Rect));
    }

    [Fact]
    public void CrossMonitor_FromSecondaryIntoPrimary()
    {
        _selection.Begin(new PixelPoint(2000, 100));

        _selection.End(new PixelPoint(1500, 300));

        Assert.Same(Monitors.Right150, _selection.Monitor);
        Assert.Equal(new PixelRect(1920, 100, 81, 201), _selection.Rect);
    }

    [Fact]
    public void NegativeMonitor_ReverseDrag()
    {
        _selection.Begin(new PixelPoint(-100, 1000));
        _selection.End(new PixelPoint(-900, -500)); // 超出上沿（-180）被夹紧

        Assert.Same(Monitors.Left125, _selection.Monitor);
        Assert.Equal(new PixelRect(-900, -180, 801, 1181), _selection.Rect);
        Assert.Equal(RegionSelectionState.Completed, _selection.State);
    }

    [Fact]
    public void NegativeMonitor_DragRightStopsBeforePrimary()
    {
        _selection.Begin(new PixelPoint(-50, 0));
        _selection.End(new PixelPoint(400, 40));

        Assert.Equal(new PixelRect(-50, 0, 50, 41), _selection.Rect);
    }

    [Fact]
    public void BeginInGap_UsesNearestMonitor()
    {
        _selection.Begin(new PixelPoint(1000, 1300)); // 主屏下方空隙，起点夹紧到 (1000, 1079)

        Assert.Same(Monitors.Primary100, _selection.Monitor);
        _selection.End(new PixelPoint(1200, 900));
        Assert.Equal(new PixelRect(1000, 900, 201, 180), _selection.Rect);
    }

    [Fact]
    public void Cancel_WhileIdle_And_Dragging()
    {
        _selection.Cancel();
        Assert.Equal(RegionSelectionState.Cancelled, _selection.State);

        var dragging = new RegionSelection(Layout);
        dragging.Begin(new PixelPoint(0, 0));
        dragging.Move(new PixelPoint(500, 500));
        dragging.Cancel();
        Assert.Equal(RegionSelectionState.Cancelled, dragging.State);
    }

    [Fact]
    public void AfterFinish_InputIgnored()
    {
        _selection.Begin(new PixelPoint(0, 0));
        _selection.End(new PixelPoint(100, 100));
        var rect = _selection.Rect;
        var changes = _changes;

        Assert.False(_selection.Begin(new PixelPoint(500, 500)));
        Assert.False(_selection.Move(new PixelPoint(600, 600)));
        Assert.Equal(RegionSelectionState.Completed, _selection.End(new PixelPoint(700, 700)));
        _selection.Cancel();

        Assert.Equal(RegionSelectionState.Completed, _selection.State);
        Assert.Equal(rect, _selection.Rect);
        Assert.Equal(changes, _changes);
    }

    [Fact]
    public void MoveOrEnd_BeforeBegin_Ignored()
    {
        Assert.False(_selection.Move(new PixelPoint(5, 5)));
        Assert.Equal(RegionSelectionState.Idle, _selection.End(new PixelPoint(5, 5)));
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void SecondBegin_WhileDragging_Ignored()
    {
        _selection.Begin(new PixelPoint(10, 10));

        Assert.False(_selection.Begin(new PixelPoint(2000, 10)));
        Assert.Same(Monitors.Primary100, _selection.Monitor);
    }

    [Fact]
    public void IsLargeEnough_TracksDrag()
    {
        _selection.Begin(new PixelPoint(0, 0));
        _selection.Move(new PixelPoint(3, 3));
        Assert.False(_selection.IsLargeEnough);

        _selection.Move(new PixelPoint(7, 7));
        Assert.True(_selection.IsLargeEnough);
    }

    [Fact]
    public void Arguments_Validated()
    {
        Assert.Throws<ArgumentNullException>(() => new RegionSelection(null!));
        Assert.Throws<ArgumentException>(() => new RegionSelection([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegionSelection(Layout, 0));
    }
}
