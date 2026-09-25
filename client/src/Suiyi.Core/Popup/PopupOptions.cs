namespace Suiyi.Core.Popup;

/// <summary>浮窗参数，由设置 <c>popup.*</c> 映射（<c>PopupSettings.ToPopupOptions</c>）。</summary>
public sealed record PopupOptions
{
    /// <summary>最大宽度（DIP），默认 480。</summary>
    public double MaxWidth { get; init; } = 480;

    /// <summary>自动消失秒数，默认 8；0 表示不自动消失。</summary>
    public int AutoHideSeconds { get; init; } = 8;

    /// <summary>距光标的偏移（DIP），默认 16。</summary>
    public double CursorOffset { get; init; } = 16;

    /// <summary>加载指示延迟，默认 300 ms。</summary>
    public TimeSpan LoadingIndicatorDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>「已复制」反馈时长，默认 1 s。</summary>
    public TimeSpan CopiedFeedbackDuration { get; init; } = TimeSpan.FromSeconds(1);
}
