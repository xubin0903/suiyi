using System.Runtime.InteropServices;
using Suiyi.Core.Capture;
using static Suiyi.App.Interop.ScreenCaptureNativeMethods;
using Rect = Suiyi.App.Interop.PopupNativeMethods.Rect;

namespace Suiyi.App.Capture;

/// <summary>枚举显示器：物理像素边界（虚拟桌面坐标）与有效 DPI。进程为 Per-Monitor V2，取到的都是物理值。</summary>
internal static unsafe class MonitorEnumerator
{
    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var handles = new List<IntPtr>();
        var gc = GCHandle.Alloc(handles);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, &OnMonitor, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        var monitors = new List<DisplayMonitor>(handles.Count);
        foreach (var handle in handles)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfoEx(handle, ref info))
            {
                continue;
            }

            var dpi = GetDpiForMonitor(handle, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0 ? (int)dpiX : DisplayMonitor.BaseDpi;
            var name = new string(info.DeviceName).TrimEnd('\0');
            var bounds = PixelRect.FromEdges(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
            monitors.Add(new DisplayMonitor(name, bounds, dpi, (info.Flags & MonitorInfoFPrimary) != 0));
        }

        return monitors;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(IntPtr monitor, IntPtr hdc, Rect* rect, IntPtr data)
    {
        if (GCHandle.FromIntPtr(data).Target is List<IntPtr> list)
        {
            list.Add(monitor);
        }

        return 1;
    }
}
