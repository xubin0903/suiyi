using System.Text;

namespace Suiyi.Core.Hotkeys;

/// <summary>一个全局快捷键：修饰键 + 一个按键（Win32 虚拟键码）。</summary>
/// <param name="Modifiers">修饰键。</param>
/// <param name="VirtualKey">虚拟键码（<c>VK_*</c>）。</param>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    /// <summary>规范写法，修饰键顺序固定为 Ctrl+Alt+Shift+Win，例如 <c>Ctrl+Alt+T</c>。</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        Append(builder, HotkeyModifiers.Control, "Ctrl");
        Append(builder, HotkeyModifiers.Alt, "Alt");
        Append(builder, HotkeyModifiers.Shift, "Shift");
        Append(builder, HotkeyModifiers.Win, "Win");
        builder.Append(HotkeyKeys.GetName(VirtualKey));
        return builder.ToString();
    }

    private void Append(StringBuilder builder, HotkeyModifiers flag, string name)
    {
        if ((Modifiers & flag) != 0)
        {
            builder.Append(name).Append('+');
        }
    }
}
