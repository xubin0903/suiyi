using System.Runtime.InteropServices;
using System.Windows.Interop;
using Suiyi.Core.Hotkeys;
using static Suiyi.App.Interop.KeyboardNativeMethods;

namespace Suiyi.App.Interop;

/// <summary>
/// 基于 <c>RegisterHotKey</c> 的全局快捷键注册（<c>MOD_NOREPEAT</c>）。
/// 自建一个仅消息窗口接收 <c>WM_HOTKEY</c>。必须在 UI 线程创建与使用。
/// </summary>
internal sealed class Win32HotkeyRegistrar : IHotkeyRegistrar, IDisposable
{
    private const int HotkeyId = 0x5359; // "SY"

    private readonly HwndSource _window;
    private bool _registered;
    private bool _disposed;

    public Win32HotkeyRegistrar()
    {
        var parameters = new HwndSourceParameters("SuiyiHotkeyListener")
        {
            ParentWindow = ClipboardNativeMethods.HwndMessage,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    public bool TryRegister(HotkeyGesture gesture, out int errorCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister();

        var modifiers = (uint)gesture.Modifiers | ModNoRepeat;
        if (RegisterHotKey(_window.Handle, HotkeyId, modifiers, (uint)gesture.VirtualKey))
        {
            _registered = true;
            errorCode = 0;
            return true;
        }

        errorCode = Marshal.GetLastPInvokeError();
        return false;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        UnregisterHotKey(_window.Handle, HotkeyId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unregister();
        _window.RemoveHook(WndProc);
        _window.Dispose();
        _disposed = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }
}
