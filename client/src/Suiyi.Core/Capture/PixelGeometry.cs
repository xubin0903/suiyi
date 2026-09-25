using System.Globalization;

namespace Suiyi.Core.Capture;

/// <summary>屏幕物理像素坐标（虚拟桌面坐标系，主屏左上角为原点，副屏在左 / 上方时为负）。</summary>
/// <param name="X">横坐标。</param>
/// <param name="Y">纵坐标。</param>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>物理像素矩形，左上角 + 宽高；<see cref="Right"/> / <see cref="Bottom"/> 不含。</summary>
/// <param name="X">左边。</param>
/// <param name="Y">上边。</param>
/// <param name="Width">宽（≥ 0）。</param>
/// <param name="Height">高（≥ 0）。</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>右边（不含）。</summary>
    public int Right => X + Width;

    /// <summary>下边（不含）。</summary>
    public int Bottom => Y + Height;

    /// <summary>宽或高为 0。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>由左上、右下（不含）边界构造；右下小于左上时结果为空矩形。</summary>
    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>
    /// 两个像素点（都包含在内）张成的矩形，与拖拽方向无关：(100,100) 到 (50,60) 得到 (50,60) 起 51×41。
    /// </summary>
    public static PixelRect FromCorners(PixelPoint a, PixelPoint b) => FromEdges(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Max(a.X, b.X) + 1,
        Math.Max(a.Y, b.Y) + 1);

    /// <summary>点是否在矩形内（右下边界不含）。</summary>
    public bool Contains(PixelPoint point) => point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;

    /// <summary>是否完整包含另一矩形。</summary>
    public bool Contains(PixelRect other) => other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>交集；不相交时为空矩形。</summary>
    public PixelRect Intersect(PixelRect other) => FromEdges(
        Math.Max(X, other.X),
        Math.Max(Y, other.Y),
        Math.Min(Right, other.Right),
        Math.Min(Bottom, other.Bottom));

    /// <summary>并集外接矩形。</summary>
    public PixelRect Union(PixelRect other) => FromEdges(
        Math.Min(X, other.X),
        Math.Min(Y, other.Y),
        Math.Max(Right, other.Right),
        Math.Max(Bottom, other.Bottom));

    /// <summary>把点夹紧到矩形内（最右 / 最下取 <c>Right - 1</c> / <c>Bottom - 1</c>）。空矩形时返回左上角。</summary>
    public PixelPoint Clamp(PixelPoint point) => IsEmpty
        ? new PixelPoint(X, Y)
        : new PixelPoint(Math.Clamp(point.X, X, Right - 1), Math.Clamp(point.Y, Y, Bottom - 1));

    /// <summary>平移。</summary>
    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>例如 <c>(-1920,0) 1920×1080</c>。</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X},{Y}) {Width}×{Height}");
}

/// <summary>窗口内设备无关像素（DIP，1/96 英寸）坐标。</summary>
/// <param name="X">横坐标。</param>
/// <param name="Y">纵坐标。</param>
public readonly record struct DipPoint(double X, double Y);

/// <summary>窗口内设备无关像素矩形。</summary>
/// <param name="X">左边。</param>
/// <param name="Y">上边。</param>
/// <param name="Width">宽。</param>
/// <param name="Height">高。</param>
public readonly record struct DipRect(double X, double Y, double Width, double Height);
