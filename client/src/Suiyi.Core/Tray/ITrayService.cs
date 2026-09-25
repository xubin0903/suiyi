namespace Suiyi.Core.Tray;

/// <summary>托盘「目标语言」切换。</summary>
public sealed class TrayTargetChangedEventArgs(string language) : EventArgs
{
    /// <summary>新的目标语言代码（<c>zh</c> / <c>en</c> / <c>ja</c>）。</summary>
    public string Language { get; } = language;
}

/// <summary>托盘「暂停监听」切换。</summary>
public sealed class TrayPauseToggledEventArgs(bool paused) : EventArgs
{
    /// <summary>切换后的值：<see langword="true"/> 表示已暂停。</summary>
    public bool Paused { get; } = paused;
}

/// <summary>托盘请求显示气泡通知。</summary>
public sealed class TrayNotificationEventArgs(string title, string message) : EventArgs
{
    /// <summary>标题。</summary>
    public string Title { get; } = title;

    /// <summary>正文。</summary>
    public string Message { get; } = message;
}

/// <summary>
/// 托盘对外契约。输入为 <c>Set*</c> 方法，输出为事件；集成层（#34）据此接到剪贴板、设置与引擎。
/// 所有成员应在 UI 线程调用，事件也在 UI 线程触发。
/// </summary>
public interface ITrayService
{
    /// <summary>「翻译剪贴板」。</summary>
    event EventHandler? TranslateClipboardRequested;

    /// <summary>「暂停监听」勾选变化。</summary>
    event EventHandler<TrayPauseToggledEventArgs>? PauseToggled;

    /// <summary>「目标语言」切换。</summary>
    event EventHandler<TrayTargetChangedEventArgs>? TargetChanged;

    /// <summary>「重启翻译服务」。</summary>
    event EventHandler? RestartEngineRequested;

    /// <summary>「打开设置文件」。</summary>
    event EventHandler? OpenSettingsRequested;

    /// <summary>「打开日志目录」。</summary>
    event EventHandler? OpenLogsRequested;

    /// <summary>「关于」。</summary>
    event EventHandler? AboutRequested;

    /// <summary>「退出」。</summary>
    event EventHandler? ExitRequested;

    /// <summary>左键单击托盘图标：再次显示最近一次浮窗。</summary>
    event EventHandler? ShowLastPopupRequested;

    /// <summary>当前显示状态。</summary>
    TrayState State { get; }

    /// <summary>设置服务状态与说明（如异常原因）。</summary>
    void SetStatus(TrayStatus status, string? detail = null);

    /// <summary>同步「暂停监听」勾选（不触发 <see cref="PauseToggled"/>）。</summary>
    void SetPaused(bool paused);

    /// <summary>同步目标语言（不触发 <see cref="TargetChanged"/>）。</summary>
    void SetTarget(string language);

    /// <summary>显示一次气泡通知（例如快捷键被占用、已在运行）。</summary>
    void ShowNotification(string title, string message);
}
