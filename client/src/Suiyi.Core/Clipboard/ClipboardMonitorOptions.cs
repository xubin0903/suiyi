namespace Suiyi.Core.Clipboard;

/// <summary>剪贴板监听选项。由集成层从设置映射（#27 的 <c>clipboard.*</c>）。</summary>
public sealed record ClipboardMonitorOptions
{
    /// <summary>默认选项。</summary>
    public static ClipboardMonitorOptions Default { get; } = new();

    /// <summary>去抖时长，默认 150 ms（设置允许 50–1000 ms）。</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>文本过滤选项。</summary>
    public ClipboardFilterOptions Filter { get; init; } = ClipboardFilterOptions.Default;

    /// <summary>剪贴板被占用时的重试次数，默认 3。</summary>
    public int BusyRetryCount { get; init; } = 3;

    /// <summary>重试间隔，默认 30 ms。</summary>
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(30);

    /// <summary>检查取值是否合法，不合法时抛出 <see cref="ArgumentOutOfRangeException"/>。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Debounce, TimeSpan.Zero, nameof(Debounce));
        ArgumentNullException.ThrowIfNull(Filter, nameof(Filter));
        Filter.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(BusyRetryCount, nameof(BusyRetryCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(BusyRetryDelay, TimeSpan.Zero, nameof(BusyRetryDelay));
    }
}
