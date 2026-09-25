namespace Suiyi.Core.Clipboard;

/// <summary>
/// 剪贴板文本过滤选项。由集成层从设置映射（#27 的 <c>clipboard.minChars</c> / <c>maxChars</c>）。
/// </summary>
public sealed record ClipboardFilterOptions
{
    /// <summary>默认选项：2–2000 字，去重窗口 2 秒。</summary>
    public static ClipboardFilterOptions Default { get; } = new();

    /// <summary>最少字符数（按 Unicode 码位计），默认 2。</summary>
    public int MinChars { get; init; } = 2;

    /// <summary>最多字符数（按 Unicode 码位计，与服务端一致），默认 2000。</summary>
    public int MaxChars { get; init; } = 2000;

    /// <summary>相同文本在该时间内再次出现时拒绝；<see cref="TimeSpan.Zero"/> 表示不去重。默认 2 秒。</summary>
    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>检查取值是否合法，不合法时抛出 <see cref="ArgumentOutOfRangeException"/>。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MinChars, 1, nameof(MinChars));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxChars, MinChars, nameof(MaxChars));
        ArgumentOutOfRangeException.ThrowIfLessThan(DuplicateWindow, TimeSpan.Zero, nameof(DuplicateWindow));
    }
}
