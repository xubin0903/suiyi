using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using Suiyi.Core.Tray;

namespace Suiyi.App.Tray;

/// <summary>
/// 运行时绘制托盘占位图标：状态色圆角方块 + 白色「译」字，打包成含 16/20/24/32/48 px 的多尺寸 .ico。
/// 不依赖外部图片资源（均为本仓库代码生成，随 MIT 许可）。实色底 + 白字在浅色、深色任务栏上都可辨识。
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 48];

    /// <summary>各状态的底色。</summary>
    public static Color GetColor(TrayStatus status) => status switch
    {
        TrayStatus.Ready => Color.FromArgb(0x1E, 0x6F, 0xD9),     // 蓝：就绪
        TrayStatus.Preparing => Color.FromArgb(0xD9, 0x8C, 0x00), // 橙：正在准备
        TrayStatus.Paused => Color.FromArgb(0x6E, 0x6E, 0x6E),    // 灰：已暂停
        _ => Color.FromArgb(0xC4, 0x2B, 0x1C),                     // 红：异常
    };

    /// <summary>生成某状态的多尺寸图标，Windows 按当前 DPI 选择合适尺寸。</summary>
    public static Icon Create(TrayStatus status, Size preferredSize)
    {
        using var ico = new MemoryStream();
        WriteIco(ico, status);
        ico.Position = 0;
        return new Icon(ico, preferredSize);
    }

    private static void WriteIco(Stream output, TrayStatus status)
    {
        var images = Sizes.Select(size => RenderPng(status, size)).ToArray();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

        // ICONDIR
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Length);

        // ICONDIRENTRY × n，图像数据紧随其后（PNG 压缩，Vista 起支持）。
        var offset = 6 + (16 * images.Length);
        for (var i = 0; i < images.Length; i++)
        {
            var size = Sizes[i];
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }
    }

    private static byte[] RenderPng(TrayStatus status, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var radius = Math.Max(2, size / 5);
            using (var path = RoundedRect(new RectangleF(0.5f, 0.5f, size - 1, size - 1), radius))
            using (var fill = new SolidBrush(GetColor(status)))
            {
                g.FillPath(fill, path);
            }

            using var font = new Font("Microsoft YaHei UI", size * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var text = new SolidBrush(Color.White);
            g.DrawString("译", font, text, new RectangleF(0, size * 0.04f, size, size), format);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
