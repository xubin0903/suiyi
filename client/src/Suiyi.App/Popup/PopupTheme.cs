using System.Windows.Media;
using Microsoft.Win32;

namespace Suiyi.App.Popup;

/// <summary>浮窗配色：跟随系统「应用模式」（<c>AppsUseLightTheme</c>）。</summary>
internal sealed record PopupTheme(Brush Background, Brush Border, Brush Foreground, Brush Secondary, Brush Accent, Brush ErrorForeground)
{
    public static PopupTheme Light { get; } = new(
        Frozen(0xFF, 0xFF, 0xFF), Frozen(0xD0, 0xD0, 0xD0), Frozen(0x1F, 0x1F, 0x1F),
        Frozen(0x6B, 0x6B, 0x6B), Frozen(0x1E, 0x6F, 0xD9), Frozen(0xC4, 0x2B, 0x1C));

    public static PopupTheme Dark { get; } = new(
        Frozen(0x2B, 0x2B, 0x2B), Frozen(0x45, 0x45, 0x45), Frozen(0xF0, 0xF0, 0xF0),
        Frozen(0xA8, 0xA8, 0xA8), Frozen(0x6C, 0xB4, 0xFF), Frozen(0xFF, 0x8A, 0x80));

    /// <summary>读取注册表；读不到时按浅色。</summary>
    public static PopupTheme Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? Dark : Light;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return Light;
        }
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
