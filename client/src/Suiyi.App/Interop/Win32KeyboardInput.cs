using System.Runtime.InteropServices;
using Suiyi.Core.Hotkeys;
using static Suiyi.App.Interop.KeyboardNativeMethods;

namespace Suiyi.App.Interop;

/// <summary>用 <c>GetAsyncKeyState</c> 查询修饰键、用 <c>SendInput</c> 模拟 Ctrl+C。</summary>
internal sealed class Win32KeyboardInput : IKeyboardInput
{
    private static readonly int[] ModifierKeys = [VkControl, VkMenu, VkShift, VkLWin, VkRWin];

    public bool AreModifierKeysDown()
    {
        foreach (var key in ModifierKeys)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
            {
                return true;
            }
        }

        return false;
    }

    public bool SendCopy()
    {
        Input[] inputs =
        [
            Key(VkControl, keyUp: false),
            Key(VkC, keyUp: false),
            Key(VkC, keyUp: true),
            Key(VkControl, keyUp: true),
        ];

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        return sent == inputs.Length;
    }

    private static Input Key(int virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = (ushort)virtualKey,
                Flags = keyUp ? KeyEventFKeyUp : 0,
            },
        },
    };
}
