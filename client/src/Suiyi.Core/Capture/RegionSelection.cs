namespace Suiyi.Core.Capture;

/// <summary><see cref="RegionSelection"/> 的状态。</summary>
public enum RegionSelectionState
{
    /// <summary>遮罩已显示，还没按下鼠标。</summary>
    Idle,

    /// <summary>左键按下，正在拖拽。</summary>
    Dragging,

    /// <summary>松开鼠标且选区不小于最小尺寸。</summary>
    Completed,

    /// <summary>Esc / 右键 / 选区过小 / 外部取消。</summary>
    Cancelled,
}

/// <summary>
/// 框选状态机（纯逻辑，坐标一律为屏幕物理像素）：
/// <c>Idle --按下--&gt; Dragging --松开--&gt; Completed | Cancelled</c>，任何未结束状态都可 <see cref="Cancel"/>。
/// 选区限定在按下时所在的显示器内（跨屏拖拽时裁剪到起始显示器），拖拽方向任意。
/// 宽或高小于 <see cref="MinSize"/> 物理像素视为取消（误点）。
/// </summary>
public sealed class RegionSelection
{
    /// <summary>默认最小选区边长（物理像素）。</summary>
    public const int DefaultMinSize = 8;

    private readonly IReadOnlyList<DisplayMonitor> _monitors;
    private PixelPoint _anchor;
    private PixelPoint _current;

    /// <summary>创建状态机。</summary>
    /// <param name="monitors">所有显示器（至少一台）。</param>
    /// <param name="minSize">最小选区边长（物理像素，≥ 1）。</param>
    public RegionSelection(IReadOnlyList<DisplayMonitor> monitors, int minSize = DefaultMinSize)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new ArgumentException("至少需要一台显示器", nameof(monitors));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(minSize, 1);
        _monitors = monitors;
        MinSize = minSize;
    }

    /// <summary>状态变化或选区变化（用于重绘）。</summary>
    public event EventHandler? Changed;

    /// <summary>最小选区边长。</summary>
    public int MinSize { get; }

    /// <summary>当前状态。</summary>
    public RegionSelectionState State { get; private set; } = RegionSelectionState.Idle;

    /// <summary>起始显示器；<see cref="RegionSelectionState.Idle"/> 时为 <see langword="null"/>。</summary>
    public DisplayMonitor? Monitor { get; private set; }

    /// <summary>当前选区（已规范化并裁剪到起始显示器）；没有按下时为空矩形。</summary>
    public PixelRect Rect { get; private set; }

    /// <summary>当前选区是否达到最小尺寸（松开时会完成）。</summary>
    public bool IsLargeEnough => Rect.Width >= MinSize && Rect.Height >= MinSize;

    /// <summary>是否已结束（完成或取消）。</summary>
    public bool IsFinished => State is RegionSelectionState.Completed or RegionSelectionState.Cancelled;

    /// <summary>左键按下：记录起点和起始显示器。只在 <see cref="RegionSelectionState.Idle"/> 时生效。</summary>
    /// <returns>是否进入拖拽。</returns>
    public bool Begin(PixelPoint point)
    {
        if (State != RegionSelectionState.Idle)
        {
            return false;
        }

        Monitor = ScreenCoordinates.FindMonitor(_monitors, point)!;
        _anchor = Monitor.Bounds.Clamp(point);
        _current = _anchor;
        State = RegionSelectionState.Dragging;
        Update();
        return true;
    }

    /// <summary>鼠标移动：更新选区（超出起始显示器的部分被夹紧）。只在拖拽中生效。</summary>
    /// <returns>选区是否变化。</returns>
    public bool Move(PixelPoint point)
    {
        if (State != RegionSelectionState.Dragging)
        {
            return false;
        }

        var clamped = Monitor!.Bounds.Clamp(point);
        if (clamped == _current)
        {
            return false;
        }

        _current = clamped;
        Update();
        return true;
    }

    /// <summary>左键松开：选区够大则完成，否则取消。只在拖拽中生效，其余状态原样返回。</summary>
    /// <returns>结束后的状态。</returns>
    public RegionSelectionState End(PixelPoint point)
    {
        if (State != RegionSelectionState.Dragging)
        {
            return State;
        }

        _current = Monitor!.Bounds.Clamp(point);
        Rect = PixelRect.FromCorners(_anchor, _current);
        State = IsLargeEnough ? RegionSelectionState.Completed : RegionSelectionState.Cancelled;
        Changed?.Invoke(this, EventArgs.Empty);
        return State;
    }

    /// <summary>取消（Esc、右键、窗口失去鼠标捕获、外部取消）。已结束时无副作用。</summary>
    public void Cancel()
    {
        if (IsFinished)
        {
            return;
        }

        State = RegionSelectionState.Cancelled;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Update()
    {
        Rect = PixelRect.FromCorners(_anchor, _current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
