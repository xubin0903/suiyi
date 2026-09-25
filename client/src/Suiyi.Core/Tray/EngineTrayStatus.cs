using Suiyi.Core.Engine;

namespace Suiyi.Core.Tray;

/// <summary>把翻译服务状态（#32）映射成托盘状态（纯函数）。</summary>
public static class EngineTrayStatus
{
    /// <summary>映射。</summary>
    /// <param name="change">服务状态变化。</param>
    /// <returns>托盘状态与说明（Ready 时说明为空）。</returns>
    public static (TrayStatus Status, string? Detail) Map(EngineStateChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.State switch
        {
            EngineState.Ready => (TrayStatus.Ready, null),
            EngineState.Failed => (TrayStatus.Error, change.Failure?.Message ?? change.Detail),
            _ => (TrayStatus.Preparing, change.Detail),
        };
    }

    /// <summary>该变化是否需要弹气泡通知（失败，或崩溃后正在重启）。</summary>
    /// <param name="change">服务状态变化。</param>
    public static bool ShouldNotify(EngineStateChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.State is EngineState.Failed or EngineState.Restarting;
    }
}
