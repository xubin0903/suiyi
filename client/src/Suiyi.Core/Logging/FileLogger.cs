using System.Globalization;
using System.Text;

namespace Suiyi.Core.Logging;

/// <summary>
/// 简单的文件日志：UTF-8（无 BOM），按本地日期滚动，保留最近 <see cref="RetentionDays"/> 天。
/// 线程安全；写入失败时静默丢弃，不向调用方抛出异常。
/// </summary>
public sealed class FileLogger : IAppLogger, IDisposable
{
    /// <summary>默认保留天数（含当天）。</summary>
    public const int DefaultRetentionDays = 7;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private StreamWriter? _writer;
    private DateOnly _currentDate;
    private bool _disposed;

    /// <summary>创建文件日志。</summary>
    /// <param name="directory">日志目录，不存在时在首次写入时创建。</param>
    /// <param name="prefix">文件名前缀，默认 <c>client</c>。</param>
    /// <param name="retentionDays">保留天数（含当天），至少为 1。</param>
    /// <param name="timeProvider">时钟，测试时注入；默认 <see cref="TimeProvider.System"/>。</param>
    public FileLogger(
        string directory,
        string prefix = LogPaths.ClientPrefix,
        int retentionDays = DefaultRetentionDays,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);

        Directory = directory;
        Prefix = prefix;
        RetentionDays = retentionDays;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>日志目录。</summary>
    public string Directory { get; }

    /// <summary>文件名前缀。</summary>
    public string Prefix { get; }

    /// <summary>保留天数（含当天）。</summary>
    public int RetentionDays { get; }

    /// <summary>当前（或下一次写入将使用的）日志文件完整路径。</summary>
    public string CurrentFilePath => Path.Combine(Directory, LogPaths.GetFileName(Prefix, Today()));

    /// <summary>按当前进程环境（<c>SUIYI_LOG_DIR</c> 或 <c>%LOCALAPPDATA%\suiyi\logs</c>）创建客户端日志。</summary>
    public static FileLogger CreateDefault() => new(LogPaths.ResolveDirectory());

    /// <inheritdoc />
    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        var now = _timeProvider.GetLocalNow();
        var line = Format(now, level, message, exception);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var writer = EnsureWriter(DateOnly.FromDateTime(now.DateTime));
                writer.Write(line);
                writer.Flush();
            }
            catch (IOException)
            {
                CloseWriter();
            }
            catch (UnauthorizedAccessException)
            {
                CloseWriter();
            }
        }
    }

    /// <summary>删除超过保留期的日志文件。滚动到新的一天时会自动调用。</summary>
    public void CleanupOldFiles()
    {
        lock (_gate)
        {
            CleanupOldFilesCore(Today());
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriter();
        }
    }

    internal static string Format(DateTimeOffset timestamp, LogLevel level, string message, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        builder.Append(" [").Append(LevelName(level)).Append("] ");
        builder.Append(message);
        builder.Append('\n');
        if (exception is not null)
        {
            builder.Append(exception.ToString().ReplaceLineEndings("\n"));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => level.ToString().ToUpperInvariant(),
    };

    private DateOnly Today() => DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);

    private StreamWriter EnsureWriter(DateOnly date)
    {
        if (_writer is not null && date == _currentDate)
        {
            return _writer;
        }

        CloseWriter();
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, LogPaths.GetFileName(Prefix, date));
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, Utf8NoBom);
        _currentDate = date;
        CleanupOldFilesCore(date);
        return _writer;
    }

    private void CleanupOldFilesCore(DateOnly today)
    {
        var oldest = today.AddDays(-(RetentionDays - 1));
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return;
            }

            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, Prefix + "-*.log"))
            {
                if (LogPaths.TryParseDate(Path.GetFileName(path), Prefix, out var date) && date < oldest)
                {
                    TryDelete(path);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
        }

        _writer = null;
    }
}
