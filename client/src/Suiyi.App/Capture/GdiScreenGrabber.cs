using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Suiyi.Core.Capture;
using static Suiyi.App.Interop.ScreenCaptureNativeMethods;

namespace Suiyi.App.Capture;

/// <summary>GDI <c>BitBlt</c> 抓取屏幕区域（物理像素，1:1），结果为冻结的 <see cref="BitmapSource"/>，只在内存中。</summary>
internal static class GdiScreenGrabber
{
    /// <summary>抓取 <paramref name="bounds"/>（虚拟桌面物理像素坐标，可为负）。</summary>
    /// <exception cref="Win32Exception">GDI 调用失败。</exception>
    public static BitmapSource Capture(PixelRect bounds)
    {
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("抓屏区域为空", nameof(bounds));
        }

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            throw new Win32Exception("GetDC 失败");
        }

        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new Win32Exception("创建 GDI 位图失败");
            }

            var old = SelectObject(memory, bitmap);
            var ok = BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, SrcCopy | CaptureBlt);
            var error = Marshal.GetLastPInvokeError();
            SelectObject(memory, old);
            if (!ok)
            {
                throw new Win32Exception(error, "BitBlt 失败");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memory != IntPtr.Zero)
            {
                DeleteDC(memory);
            }

            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>从冻结帧裁出 <paramref name="region"/>（位图内像素坐标）并编码为 PNG。可在后台线程调用。</summary>
    public static byte[] EncodePng(BitmapSource frame, PixelRect region)
    {
        var cropped = new CroppedBitmap(frame, new Int32Rect(region.X, region.Y, region.Width, region.Height));
        cropped.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
