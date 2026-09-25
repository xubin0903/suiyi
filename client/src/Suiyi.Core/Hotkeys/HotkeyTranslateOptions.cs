using Suiyi.Core.Clipboard;

namespace Suiyi.Core.Hotkeys;

/// <summary>快捷键翻译流程的时间参数与过滤选项。</summary>
public sealed record HotkeyTranslateOptions
{
    /// <summary>默认选项。</summary>
    public static HotkeyTranslateOptions Default { get; } = new();

    /// <summary>等待用户松开修饰键的上限，默认 500 ms；超时后仍继续。</summary>
    public TimeSpan ModifierReleaseTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>轮询间隔（修饰键、剪贴板序号），默认 10 ms。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>模拟 Ctrl+C 后等待剪贴板序号变化的上限，默认 300 ms。</summary>
    public TimeSpan CopyTimeout { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 让剪贴板监听忽略模拟复制引起的变化的窗口，默认 1000 ms
    /// （需覆盖复制等待 300 ms + 监听去抖 150 ms，并留余量）。
    /// </summary>
    public TimeSpan SuppressWindow { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// 过滤选项：上限放宽到 10000 字（与服务端默认一致），不做去重（手动触发意图明确）。
    /// </summary>
    public ClipboardFilterOptions Filter { get; init; } =
        ClipboardFilterOptions.Default with { MaxChars = 10000, DuplicateWindow = TimeSpan.Zero };

    /// <summary>剪贴板被占用时的重试次数，默认 3。</summary>
    public int BusyRetryCount { get; init; } = 3;

    /// <summary>重试间隔，默认 30 ms。</summary>
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(30);

    /// <summary>检查取值是否合法。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ModifierReleaseTimeout, TimeSpan.Zero, nameof(ModifierReleaseTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(PollInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(CopyTimeout, TimeSpan.Zero, nameof(CopyTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SuppressWindow, TimeSpan.Zero, nameof(SuppressWindow));
        ArgumentNullException.ThrowIfNull(Filter, nameof(Filter));
        Filter.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(BusyRetryCount, nameof(BusyRetryCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(BusyRetryDelay, TimeSpan.Zero, nameof(BusyRetryDelay));
    }
}
