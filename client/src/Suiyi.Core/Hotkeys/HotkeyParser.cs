namespace Suiyi.Core.Hotkeys;

/// <summary>
/// 快捷键字符串的解析与格式化（设置 <c>hotkey.translate</c>）。
/// 形如 <c>Ctrl+Alt+T</c>、<c>Ctrl+Shift+F1</c>、<c>Win+Alt+Y</c>；大小写与空格不敏感；空字符串表示禁用。
/// </summary>
public static class HotkeyParser
{
    /// <summary>默认的「翻译」快捷键。</summary>
    public const string DefaultTranslate = "Ctrl+Alt+T";

    /// <summary>
    /// 解析快捷键。成功时 <paramref name="gesture"/> 为结果；空白或 <see langword="null"/> 视为「禁用」，
    /// 此时也返回 <see langword="true"/>，<paramref name="gesture"/> 为 <see langword="null"/>。
    /// 失败时 <paramref name="error"/> 为中文原因。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture? gesture, out string? error)
    {
        gesture = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                error = "快捷键格式不正确：有空的按键段";
                return false;
            }

            if (TryGetModifier(token, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (!HotkeyKeys.TryGetVirtualKey(token, out var virtualKey))
            {
                error = $"未知键名：{token}";
                return false;
            }

            if (key is not null)
            {
                error = "快捷键只能包含一个非修饰键";
                return false;
            }

            key = virtualKey;
        }

        if (key is null)
        {
            error = "快捷键缺少非修饰键";
            return false;
        }

        const HotkeyModifiers Strong = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win;
        if ((modifiers & Strong) == 0 && !HotkeyKeys.IsFunctionKey(key.Value))
        {
            error = "除 F1–F24 外，快捷键必须包含 Ctrl、Alt 或 Win";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key.Value);
        return true;
    }

    /// <summary>规范化写法；空白返回空字符串，非法时抛出 <see cref="FormatException"/>。</summary>
    public static string Normalize(string? text)
    {
        if (!TryParse(text, out var gesture, out var error))
        {
            throw new FormatException(error);
        }

        return gesture?.ToString() ?? string.Empty;
    }

    private static bool TryGetModifier(string token, out HotkeyModifiers modifier)
    {
        modifier = token.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => HotkeyModifiers.Control,
            "ALT" => HotkeyModifiers.Alt,
            "SHIFT" => HotkeyModifiers.Shift,
            "WIN" or "WINDOWS" => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None,
        };
        return modifier != HotkeyModifiers.None;
    }
}
