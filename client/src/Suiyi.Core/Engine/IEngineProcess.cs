namespace Suiyi.Core.Engine;

/// <summary>被监管的服务进程（对 <see cref="System.Diagnostics.Process"/> 的最小抽象，便于测试）。</summary>
public interface IEngineProcess : IDisposable
{
    /// <summary>进程 id。</summary>
    int Id { get; }

    /// <summary>进程是否已退出。</summary>
    bool HasExited { get; }

    /// <summary>退出码；未退出时为 <see langword="null"/>。</summary>
    int? ExitCode { get; }

    /// <summary>等待进程退出。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>结束进程及其全部子进程。进程已退出时不做任何事，不抛异常。</summary>
    void Kill();
}

/// <summary>服务输出的一行。</summary>
/// <param name="Text">内容（不含换行）。</param>
/// <param name="IsError">是否来自 stderr。</param>
public readonly record struct EngineOutputLine(string Text, bool IsError);

/// <summary>拉起服务进程。</summary>
public interface IEngineProcessLauncher
{
    /// <summary>按 <paramref name="command"/> 启动进程。</summary>
    /// <param name="command">命令。</param>
    /// <param name="onOutput">每读到一行 stdout / stderr 时回调（可能在线程池线程上）。</param>
    /// <returns>已启动的进程。</returns>
    /// <exception cref="EngineLaunchException">可执行文件不存在或无法启动。</exception>
    IEngineProcess Start(EngineCommand command, Action<EngineOutputLine> onOutput);
}

/// <summary>无法启动服务进程（例如找不到 Python）。</summary>
public sealed class EngineLaunchException : Exception
{
    /// <summary>创建异常。</summary>
    public EngineLaunchException()
    {
    }

    /// <summary>创建异常。</summary>
    /// <param name="message">说明。</param>
    public EngineLaunchException(string message)
        : base(message)
    {
    }

    /// <summary>创建异常。</summary>
    /// <param name="message">说明。</param>
    /// <param name="innerException">原始异常。</param>
    public EngineLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
