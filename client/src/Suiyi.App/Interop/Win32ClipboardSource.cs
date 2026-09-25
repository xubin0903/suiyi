using System.Runtime.InteropServices;
using System.Windows.Interop;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Logging;
using static Suiyi.App.Interop.ClipboardNativeMethods;

namespace Suiyi.App.Interop;

/// <summary>
/// 基于 Win32 的剪贴板来源：仅消息窗口（父窗口 <c>HWND_MESSAGE</c>）+ <c>AddClipboardFormatListener</c>。
/// 直接用 Win32 读写，而不是 WPF <c>Clipboard</c>：后者遇到剪贴板被占用会抛 <c>COMException</c>，
/// 也读不到隐私标记格式。必须在 UI（STA）线程创建与使用。
/// </summary>
internal sealed class Win32ClipboardSource : IClipboardSource, IDisposable
{
    private const int WriteRetryCount = 3;
    private const int WriteRetryDelayMs = 30;

    // 隐私格式：https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats
    private readonly uint _excludeFromMonitorFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint _canIncludeInHistoryFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private readonly uint _canUploadToCloudFormat = RegisterClipboardFormat("CanUploadToCloudClipboard");

    private readonly IAppLogger _logger;
    private readonly HwndSource _window;
    private bool _listening;
    private bool _disposed;

    public Win32ClipboardSource(IAppLogger logger)
    {
        _logger = logger;
        var parameters = new HwndSourceParameters("SuiyiClipboardListener")
        {
            ParentWindow = HwndMessage,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
    }

    public event EventHandler? Changed;

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listening)
        {
            return;
        }

        if (AddClipboardFormatListener(_window.Handle))
        {
            _listening = true;
        }
        else
        {
            _logger.Error($"剪贴板：AddClipboardFormatListener 失败，错误码 {Marshal.GetLastPInvokeError()}");
        }
    }

    public void StopListening()
    {
        if (!_listening)
        {
            return;
        }

        _listening = false;
        if (!RemoveClipboardFormatListener(_window.Handle))
        {
            _logger.Warn($"剪贴板：RemoveClipboardFormatListener 失败，错误码 {Marshal.GetLastPInvokeError()}");
        }
    }

    public uint GetSequenceNumber() => GetClipboardSequenceNumber();

    public ClipboardReadResult TryReadText()
    {
        // 不打开剪贴板就能判断的先判断：带排除标记或没有文本时不读正文。
        if (_excludeFromMonitorFormat != 0 && IsClipboardFormatAvailable(_excludeFromMonitorFormat))
        {
            return ClipboardReadResult.PrivateContent;
        }

        if (!IsClipboardFormatAvailable(CfUnicodeText))
        {
            return ClipboardReadResult.NoText;
        }

        if (!OpenClipboard(_window.Handle))
        {
            return ClipboardReadResult.Busy;
        }

        try
        {
            if (IsDwordZero(_canIncludeInHistoryFormat) || IsDwordZero(_canUploadToCloudFormat))
            {
                return ClipboardReadResult.PrivateContent;
            }

            var text = ReadUnicodeText();
            return text is null ? ClipboardReadResult.NoText : ClipboardReadResult.FromText(text);
        }
        finally
        {
            CloseClipboard();
        }
    }

    public bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var opened = OpenClipboard(_window.Handle);
        for (var attempt = 0; !opened && attempt < WriteRetryCount; attempt++)
        {
            Thread.Sleep(WriteRetryDelayMs);
            opened = OpenClipboard(_window.Handle);
        }

        if (!opened)
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            var handle = GlobalAlloc(GmemMoveable, bytes);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                // 失败时所有权仍在本进程，需要自己释放。
                GlobalFree(handle);
                return false;
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopListening();
        _window.RemoveHook(WndProc);
        _window.Dispose();
        _disposed = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>格式存在且其 DWORD 值为 0。调用前剪贴板必须已打开。</summary>
    private static bool IsDwordZero(uint format)
    {
        if (format == 0 || !IsClipboardFormatAvailable(format))
        {
            return false;
        }

        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return GlobalSize(handle) >= sizeof(int) && Marshal.ReadInt32(pointer) == 0;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>读取 <c>CF_UNICODETEXT</c>，按内存块大小截断，不依赖结尾的 NUL。调用前剪贴板必须已打开。</summary>
    private static string? ReadUnicodeText()
    {
        var handle = GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var maxChars = (int)Math.Min((ulong)GlobalSize(handle) / sizeof(char), int.MaxValue);
            var text = Marshal.PtrToStringUni(pointer, maxChars);
            var nul = text.IndexOf('\0', StringComparison.Ordinal);
            return nul >= 0 ? text[..nul] : text;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }
}
