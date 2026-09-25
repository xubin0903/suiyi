using Suiyi.Core.Hotkeys;

namespace Suiyi.Core.Tests.Hotkeys;

public class HotkeyParserTests
{
    [Theory]
    [InlineData("Ctrl+Alt+T", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54, "Ctrl+Alt+T")]
    [InlineData("ctrl + alt + t", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54, "Ctrl+Alt+T")]
    [InlineData(" Alt+Ctrl+T ", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54, "Ctrl+Alt+T")]
    [InlineData("Control+Shift+F1", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x70, "Ctrl+Shift+F1")]
    [InlineData("Win+Alt+Y", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x59, "Alt+Win+Y")]
    [InlineData("WINDOWS+SHIFT+Y", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x59, "Shift+Win+Y")]
    [InlineData("Ctrl+Shift+Y", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x59, "Ctrl+Shift+Y")]
    [InlineData("Ctrl+Alt+1", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x31, "Ctrl+Alt+1")]
    [InlineData("Ctrl+Alt+Space", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20, "Ctrl+Alt+Space")]
    [InlineData("Ctrl+Alt+PgDn", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x22, "Ctrl+Alt+PageDown")]
    [InlineData("Ctrl+Alt+Escape", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x1B, "Ctrl+Alt+Esc")]
    [InlineData("Alt+NumPad5", HotkeyModifiers.Alt, 0x65, "Alt+NumPad5")]
    [InlineData("F8", HotkeyModifiers.None, 0x77, "F8")]
    [InlineData("Shift+F24", HotkeyModifiers.Shift, 0x87, "Shift+F24")]
    [InlineData("Ctrl+Ctrl+T", HotkeyModifiers.Control, 0x54, "Ctrl+T")]
    public void TryParse_ValidGestures(string text, HotkeyModifiers modifiers, int virtualKey, string canonical)
    {
        Assert.True(HotkeyParser.TryParse(text, out var gesture, out var error), error);

        Assert.Null(error);
        Assert.Equal(new HotkeyGesture(modifiers, virtualKey), gesture);
        Assert.Equal(canonical, gesture!.Value.ToString());
        Assert.Equal(canonical, HotkeyParser.Normalize(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_BlankMeansDisabled(string? text)
    {
        Assert.True(HotkeyParser.TryParse(text, out var gesture, out var error));

        Assert.Null(gesture);
        Assert.Null(error);
        Assert.Equal(string.Empty, HotkeyParser.Normalize(text));
    }

    [Theory]
    [InlineData("T", "必须包含")]
    [InlineData("Shift+T", "必须包含")]
    [InlineData("Space", "必须包含")]
    [InlineData("Ctrl+Alt", "缺少非修饰键")]
    [InlineData("Ctrl+", "空的按键段")]
    [InlineData("Ctrl++T", "空的按键段")]
    [InlineData("Ctrl+Foo", "未知键名：Foo")]
    [InlineData("Ctrl+Alt+Ä", "未知键名")]
    [InlineData("Ctrl+A+B", "只能包含一个")]
    [InlineData("Ctrl+F25", "未知键名")]
    public void TryParse_InvalidGestures(string text, string expectedError)
    {
        Assert.False(HotkeyParser.TryParse(text, out var gesture, out var error));

        Assert.Null(gesture);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => HotkeyParser.Normalize(text));
    }

    [Fact]
    public void DefaultTranslate_IsCtrlAltT()
    {
        Assert.Equal("Ctrl+Alt+T", HotkeyParser.DefaultTranslate);
        Assert.True(HotkeyParser.TryParse(HotkeyParser.DefaultTranslate, out var gesture, out _));
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'T'), gesture);
    }

    [Fact]
    public void Modifiers_MatchWin32Values()
    {
        Assert.Equal(0x1, (int)HotkeyModifiers.Alt);
        Assert.Equal(0x2, (int)HotkeyModifiers.Control);
        Assert.Equal(0x4, (int)HotkeyModifiers.Shift);
        Assert.Equal(0x8, (int)HotkeyModifiers.Win);
    }

    [Fact]
    public void GetName_UnknownKeyUsesHex()
    {
        Assert.Equal("0xBA", HotkeyKeys.GetName(0xBA));
    }
}
