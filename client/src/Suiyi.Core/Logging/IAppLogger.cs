namespace Suiyi.Core.Logging;

/// <summary>
/// 客户端最小日志接口。实现不得抛出异常，也不得记录剪贴板正文等用户内容。
/// </summary>
public interface IAppLogger
{
    /// <summary>写一条日志。</summary>
    /// <param name="level">级别。</param>
    /// <param name="message">消息（单行为宜）。</param>
    /// <param name="exception">可选的异常，会附在消息之后。</param>
    void Log(LogLevel level, string message, Exception? exception = null);
}

/// <summary><see cref="IAppLogger"/> 的便捷方法。</summary>
public static class AppLoggerExtensions
{
    /// <summary>写一条 <see cref="LogLevel.Info"/> 日志。</summary>
    public static void Info(this IAppLogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(LogLevel.Info, message);
    }

    /// <summary>写一条 <see cref="LogLevel.Warning"/> 日志。</summary>
    public static void Warn(this IAppLogger logger, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(LogLevel.Warning, message, exception);
    }

    /// <summary>写一条 <see cref="LogLevel.Error"/> 日志。</summary>
    public static void Error(this IAppLogger logger, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(LogLevel.Error, message, exception);
    }
}

/// <summary>丢弃所有日志的实现，供测试或尚未初始化日志时使用。</summary>
public sealed class NullAppLogger : IAppLogger
{
    /// <summary>共享实例。</summary>
    public static NullAppLogger Instance { get; } = new();

    private NullAppLogger()
    {
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string message, Exception? exception = null)
    {
    }
}
