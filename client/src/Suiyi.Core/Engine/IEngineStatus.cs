namespace Suiyi.Core.Engine;

/// <summary><see cref="EngineSupervisor"/> 对编排逻辑（#34）暴露的状态面。</summary>
public interface IEngineStatus
{
    /// <summary>状态变化（可能在线程池线程上触发）。</summary>
    event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    /// <summary>当前状态。</summary>
    EngineState State { get; }

    /// <summary>失败原因（<see cref="EngineState.Failed"/> 时）。</summary>
    EngineFailure? Failure { get; }

    /// <summary>请求尽快做一次健康检查（例如翻译请求连接被拒），不必等看门狗间隔。</summary>
    void RequestHealthCheck();
}
