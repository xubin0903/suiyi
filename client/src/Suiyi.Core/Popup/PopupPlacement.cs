namespace Suiyi.Core.Popup;

/// <summary>屏幕坐标点（物理像素，可为负：主显示器左 / 上方的显示器）。</summary>
public readonly record struct PopupPoint(double X, double Y);

/// <summary>尺寸（物理像素）。</summary>
public readonly record struct PopupSize(double Width, double Height);

/// <summary>矩形（物理像素），例如显示器工作区。</summary>
public readonly record struct PopupRect(double Left, double Top, double Width, double Height)
{
    /// <summary>右边界（不含）。</summary>
    public double Right => Left + Width;

    /// <summary>下边界（不含）。</summary>
    public double Bottom => Top + Height;
}

/// <summary><see cref="PopupPlacement.CalculateAroundRect"/> 选中的位置。</summary>
public enum RectPlacementSide
{
    /// <summary>选区右下角外侧。</summary>
    BottomRight,

    /// <summary>选区下方。</summary>
    Below,

    /// <summary>选区上方。</summary>
    Above,

    /// <summary>选区左侧。</summary>
    Left,

    /// <summary>四个方向都放不下，压住选区一部分（取下 / 上空间较大的一侧后夹紧）。</summary>
    Overlap,
}

/// <summary>以矩形为锚点的放置结果。</summary>
/// <param name="Position">窗口左上角（物理像素）。</param>
/// <param name="Side">采用的位置。</param>
public readonly record struct RectPlacement(PopupPoint Position, RectPlacementSide Side);

/// <summary>浮窗位置计算（纯函数）。</summary>
public static class PopupPlacement
{
    /// <summary>
    /// 默认放在光标右下方 <paramref name="offset"/> 处；右侧放不下翻到光标左侧，下方放不下翻到上方；
    /// 最后夹紧到 <paramref name="workArea"/> 内（窗口比工作区还大时贴左 / 上边）。
    /// </summary>
    /// <returns>窗口左上角。</returns>
    public static PopupPoint Calculate(PopupPoint cursor, PopupSize size, PopupRect workArea, double offset)
    {
        var x = Axis(cursor.X, size.Width, workArea.Left, workArea.Right, offset);
        var y = Axis(cursor.Y, size.Height, workArea.Top, workArea.Bottom, offset);
        return new PopupPoint(x, y);
    }

    /// <summary>
    /// 以矩形（框选选区）为锚点放置浮窗（#57），按顺序取第一个放得下的位置：
    /// <list type="number">
    /// <item><see cref="RectPlacementSide.BottomRight"/>：选区右下角外侧（左上角 = 选区右下角 + <paramref name="gap"/>），横纵都要放得下；</item>
    /// <item><see cref="RectPlacementSide.Below"/>：选区下方，与选区左边对齐（横向可左移夹紧），纵向放得下；</item>
    /// <item><see cref="RectPlacementSide.Above"/>：选区上方，同上；</item>
    /// <item><see cref="RectPlacementSide.Left"/>：选区左侧，与选区上边对齐（纵向可上移夹紧），横向放得下。</item>
    /// </list>
    /// 都放不下（浮窗比选区外的空间还大）时，放在下方与上方中空间较大的一侧并夹紧到工作区（可能压住选区一部分）。
    /// 结果总是夹紧在 <paramref name="workArea"/>（选区所在显示器的工作区）内；窗口比工作区还大时贴左 / 上边。
    /// </summary>
    /// <param name="anchor">选区（物理像素）。</param>
    /// <param name="size">浮窗尺寸（物理像素）。</param>
    /// <param name="workArea">选区所在显示器的工作区（物理像素）。</param>
    /// <param name="gap">与选区的间距（物理像素）。</param>
    public static RectPlacement CalculateAroundRect(PopupRect anchor, PopupSize size, PopupRect workArea, double gap)
    {
        var w = size.Width;
        var h = size.Height;
        bool FitsX(double x) => x >= workArea.Left && x + w <= workArea.Right;
        bool FitsY(double y) => y >= workArea.Top && y + h <= workArea.Bottom;

        var right = anchor.Right + gap;
        var below = anchor.Bottom + gap;
        var above = anchor.Top - gap - h;
        var left = anchor.Left - gap - w;

        if (FitsX(right) && FitsY(below))
        {
            return Clamped(RectPlacementSide.BottomRight, right, below);
        }

        if (FitsY(below))
        {
            return Clamped(RectPlacementSide.Below, anchor.Left, below);
        }

        if (FitsY(above))
        {
            return Clamped(RectPlacementSide.Above, anchor.Left, above);
        }

        if (FitsX(left))
        {
            return Clamped(RectPlacementSide.Left, left, anchor.Top);
        }

        var spaceBelow = workArea.Bottom - anchor.Bottom;
        var spaceAbove = anchor.Top - workArea.Top;
        return spaceBelow >= spaceAbove
            ? Clamped(RectPlacementSide.Overlap, anchor.Left, below)
            : Clamped(RectPlacementSide.Overlap, anchor.Left, above);

        RectPlacement Clamped(RectPlacementSide side, double x, double y) =>
            new(new PopupPoint(Clamp(x, w, workArea.Left, workArea.Right), Clamp(y, h, workArea.Top, workArea.Bottom)), side);
    }

    private static double Clamp(double position, double length, double min, double max)
    {
        if (position + length > max)
        {
            position = max - length;
        }

        return position < min ? min : position;
    }

    private static double Axis(double cursor, double length, double min, double max, double offset)
    {
        var position = cursor + offset;
        if (position + length > max)
        {
            position = cursor - offset - length;
        }

        if (position + length > max)
        {
            position = max - length;
        }

        if (position < min)
        {
            position = min;
        }

        return position;
    }
}
