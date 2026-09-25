namespace Suiyi.Core.Engine;

/// <summary>翻译服务的状态。</summary>
public enum EngineState
{
    /// <summary>未启动或已停止。</summary>
    Stopped,

    /// <summary>正在拉起并等待 <c>/health</c>。</summary>
    Starting,

    /// <summary>可用。</summary>
    Ready,

    /// <summary>托管的服务崩溃，正在退避后重启。</summary>
    Restarting,

    /// <summary>失败，不再自动重试；见 <see cref="EngineStateChangedEventArgs.Failure"/>。</summary>
    Failed,
}

/// <summary>服务进程归属。</summary>
public enum EngineOwnership
{
    /// <summary>没有可用服务。</summary>
    None,

    /// <summary>由本客户端拉起，退出时结束它。</summary>
    Managed,

    /// <summary>复用用户手动启动的服务，不结束它。</summary>
    External,
}

/// <summary>失败原因。</summary>
public enum EngineFailureReason
{
    /// <summary>找不到或无法启动可执行文件。</summary>
    LaunchFailed,

    /// <summary>就绪前进程退出（缺模型、端口被占用、未安装 suiyi_engine 等）。</summary>
    ExitedBeforeReady,

    /// <summary>启动超时。</summary>
    StartupTimeout,

    /// <summary>短时间内反复崩溃。</summary>
    CrashedRepeatedly,
}

/// <summary>一次失败的原因与中文提示。</summary>
/// <param name="Reason">原因。</param>
/// <param name="Message">面向用户的一句中文提示。</param>
public sealed record EngineFailure(EngineFailureReason Reason, string Message);

/// <summary><see cref="EngineSupervisor.StateChanged"/> 的参数。</summary>
public sealed class EngineStateChangedEventArgs : EventArgs
{
    /// <summary>创建。</summary>
    public EngineStateChangedEventArgs(EngineState state, EngineOwnership ownership, string detail, EngineFailure? failure = null)
    {
        State = state;
        Ownership = ownership;
        Detail = detail;
        Failure = failure;
    }

    /// <summary>新状态。</summary>
    public EngineState State { get; }

    /// <summary>服务归属。</summary>
    public EngineOwnership Ownership { get; }

    /// <summary>中文说明，可直接显示或写日志。</summary>
    public string Detail { get; }

    /// <summary><see cref="EngineState.Failed"/> 时的原因。</summary>
    public EngineFailure? Failure { get; }

    /// <inheritdoc />
    public override string ToString() =>
        Ownership == EngineOwnership.None ? $"{State}：{Detail}" : $"{State}({Ownership})：{Detail}";
}
