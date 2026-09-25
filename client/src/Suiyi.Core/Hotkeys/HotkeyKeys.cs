using System.Globalization;

namespace Suiyi.Core.Hotkeys;

/// <summary>快捷键可用的键名与 Win32 虚拟键码的对应。</summary>
public static class HotkeyKeys
{
    // 必须声明在两个字典之前：静态字段按声明顺序初始化。
    private static readonly (int Key, string[] Names)[] Named =
    [
        (0x20, ["Space"]),
        (0x0D, ["Enter", "Return"]),
        (0x09, ["Tab"]),
        (0x1B, ["Esc", "Escape"]),
        (0x08, ["Backspace"]),
        (0x2D, ["Insert", "Ins"]),
        (0x2E, ["Delete", "Del"]),
        (0x24, ["Home"]),
        (0x23, ["End"]),
        (0x21, ["PageUp", "PgUp"]),
        (0x22, ["PageDown", "PgDn"]),
        (0x25, ["Left"]),
        (0x26, ["Up"]),
        (0x27, ["Right"]),
        (0x28, ["Down"]),
        (0x13, ["Pause"]),
        (0x2C, ["PrintScreen", "PrtSc"]),
    ];

    private static readonly Dictionary<string, int> NameToKey = BuildNameToKey();
    private static readonly Dictionary<int, string> KeyToName = BuildKeyToName();

    /// <summary>按键名（大小写不敏感）查虚拟键码。</summary>
    public static bool TryGetVirtualKey(string name, out int virtualKey) =>
        NameToKey.TryGetValue(name, out virtualKey);

    /// <summary>虚拟键码的规范名；未知键码返回 <c>0x..</c> 形式。</summary>
    public static string GetName(int virtualKey) =>
        KeyToName.TryGetValue(virtualKey, out var name)
            ? name
            : "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>功能键 F1–F24 可以不带修饰键单独注册。</summary>
    public static bool IsFunctionKey(int virtualKey) => virtualKey is >= 0x70 and <= 0x87;

    private static Dictionary<string, int> BuildNameToKey()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 'A'; c <= 'Z'; c++)
        {
            map[c.ToString()] = c;
        }

        for (var d = 0; d <= 9; d++)
        {
            map[d.ToString(CultureInfo.InvariantCulture)] = 0x30 + d;
            map["NumPad" + d.ToString(CultureInfo.InvariantCulture)] = 0x60 + d;
        }

        for (var f = 1; f <= 24; f++)
        {
            map["F" + f.ToString(CultureInfo.InvariantCulture)] = 0x70 + f - 1;
        }

        // 第一个名字是规范名。
        foreach (var (key, names) in Named)
        {
            foreach (var name in names)
            {
                map[name] = key;
            }
        }

        return map;
    }

    private static Dictionary<int, string> BuildKeyToName()
    {
        var map = new Dictionary<int, string>();
        for (var c = 'A'; c <= 'Z'; c++)
        {
            map[c] = c.ToString();
        }

        for (var d = 0; d <= 9; d++)
        {
            map[0x30 + d] = d.ToString(CultureInfo.InvariantCulture);
            map[0x60 + d] = "NumPad" + d.ToString(CultureInfo.InvariantCulture);
        }

        for (var f = 1; f <= 24; f++)
        {
            map[0x70 + f - 1] = "F" + f.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var (key, names) in Named)
        {
            map[key] = names[0];
        }

        return map;
    }
}
