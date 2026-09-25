using System.Runtime.InteropServices;

namespace Suiyi.App.Interop;

/// <summary>框选截屏用到的 Win32 声明：枚举显示器与 DPI（user32 / shcore）、GDI 抓屏（gdi32）、DWM 刷新。</summary>
internal static unsafe partial class ScreenCaptureNativeMethods
{
    public const uint MonitorInfoFPrimary = 1;

    /// <summary><c>MDT_EFFECTIVE_DPI</c>。</summary>
    public const int MdtEffectiveDpi = 0;

    /// <summary><c>SRCCOPY</c>。</summary>
    public const uint SrcCopy = 0x00CC0020;

    /// <summary><c>CAPTUREBLT</c>：包含分层窗口。</summary>
    public const uint CaptureBlt = 0x40000000;

    public static readonly IntPtr HwndTopmost = new(-1);

    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, delegate* unmanaged<IntPtr, IntPtr, PopupNativeMethods.Rect*, IntPtr, int> lpfnEnum, IntPtr dwData);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoEx(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfoEx
    {
        public int Size;
        public PopupNativeMethods.Rect Monitor;
        public PopupNativeMethods.Rect Work;
        public uint Flags;
        public fixed char DeviceName[32];
    }
}
