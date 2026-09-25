namespace Suiyi.Core.Tray;

/// <summary>托盘图标状态。</summary>
public enum TrayStatus
{
    /// <summary>翻译服务正在准备（启动或重启中）。</summary>
    Preparing,

    /// <summary>就绪。</summary>
    Ready,

    /// <summary>已暂停剪贴板监听。</summary>
    Paused,

    /// <summary>翻译服务异常。</summary>
    Error,
}
