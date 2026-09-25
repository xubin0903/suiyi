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
