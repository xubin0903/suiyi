namespace Suiyi.Core.Hotkeys;

/// <summary>修饰键。取值与 Win32 <c>MOD_ALT</c> / <c>MOD_CONTROL</c> / <c>MOD_SHIFT</c> / <c>MOD_WIN</c> 一致。</summary>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>无。</summary>
    None = 0,

    /// <summary>Alt。</summary>
    Alt = 0x1,

    /// <summary>Ctrl。</summary>
    Control = 0x2,

    /// <summary>Shift。</summary>
    Shift = 0x4,

    /// <summary>Windows 徽标键。</summary>
    Win = 0x8,
}
