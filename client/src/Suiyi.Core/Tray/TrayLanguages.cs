namespace Suiyi.Core.Tray;

/// <summary>托盘「目标语言」菜单里的一项。</summary>
/// <param name="Code">ISO 639-1 代码。</param>
/// <param name="DisplayName">显示名（用该语言自己的写法）。</param>
public readonly record struct TrayLanguage(string Code, string DisplayName);

/// <summary>可选的目标语言（M2：中英日）。</summary>
public static class TrayLanguages
{
    /// <summary>菜单顺序：中文、English、日本語。</summary>
    public static IReadOnlyList<TrayLanguage> All { get; } =
    [
        new("zh", "中文"),
        new("en", "English"),
        new("ja", "日本語"),
    ];

    /// <summary>是否为支持的目标语言代码（大小写不敏感）。</summary>
    public static bool IsSupported(string? code) =>
        code is not null && All.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>显示名；未知代码原样返回。</summary>
    public static string GetDisplayName(string code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)).DisplayName ?? code;
}
