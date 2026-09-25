using System.Collections.Concurrent;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Tests.Clipboard;

internal sealed class RecordingLogger : IAppLogger
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public void Log(LogLevel level, string message, Exception? exception = null) =>
        Messages.Enqueue($"[{level}] {message} {exception}");
}
