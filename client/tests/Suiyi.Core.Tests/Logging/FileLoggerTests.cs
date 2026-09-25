using System.Text;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Tests.Logging;

public sealed class FileLoggerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Log_WritesUtf8LineWithLevelToDailyFile()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 8, 30, 0, TimeSpan.Zero));
        using (var logger = new FileLogger(_temp.Path, timeProvider: clock))
        {
            logger.Info("客户端启动");
            logger.Warn("第二行");
        }

        var path = Path.Combine(_temp.Path, "client-20260925.log");
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "不应写入 BOM");

        var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-09-25 08:30:00.000 +00:00 [INFO] 客户端启动", lines[0]);
        Assert.EndsWith("[WARN] 第二行", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Log_CreatesMissingDirectory()
    {
        var dir = Path.Combine(_temp.Path, "nested", "logs");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));

        using (var logger = new FileLogger(dir, timeProvider: clock))
        {
            logger.Info("hello");
        }

        Assert.True(File.Exists(Path.Combine(dir, "client-20260925.log")));
    }

    [Fact]
    public void Log_RollsOverToNewFileAtMidnight()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 23, 59, 59, TimeSpan.Zero));
        using (var logger = new FileLogger(_temp.Path, timeProvider: clock))
        {
            logger.Info("before");
            clock.Advance(TimeSpan.FromSeconds(2));
            logger.Info("after");
            Assert.Equal(Path.Combine(_temp.Path, "client-20260926.log"), logger.CurrentFilePath);
        }

        Assert.Contains("before", File.ReadAllText(Path.Combine(_temp.Path, "client-20260925.log")), StringComparison.Ordinal);
        var second = File.ReadAllText(Path.Combine(_temp.Path, "client-20260926.log"));
        Assert.Contains("after", second, StringComparison.Ordinal);
        Assert.DoesNotContain("before", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_AppendsToExistingFileOfSameDay()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));
        using (var first = new FileLogger(_temp.Path, timeProvider: clock))
        {
            first.Info("one");
        }

        using (var second = new FileLogger(_temp.Path, timeProvider: clock))
        {
            second.Info("two");
        }

        var lines = File.ReadAllLines(Path.Combine(_temp.Path, "client-20260925.log"));
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Log_KeepsSevenDaysAndDeletesOlderFiles()
    {
        // 今天 2026-09-25：保留 09-19 ~ 09-25，删除 09-18 及更早。其他前缀与无关文件不动。
        foreach (var name in new[]
        {
            "client-20260910.log",
            "client-20260918.log",
            "client-20260919.log",
            "client-20260924.log",
            "engine-20260901.log",
            "notes.txt",
        })
        {
            File.WriteAllText(Path.Combine(_temp.Path, name), "x");
        }

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));
        using (var logger = new FileLogger(_temp.Path, timeProvider: clock))
        {
            logger.Info("trigger");
        }

        string[] expected =
        [
            "client-20260919.log",
            "client-20260924.log",
            "client-20260925.log",
            "engine-20260901.log",
            "notes.txt",
        ];
        var remaining = Directory.GetFiles(_temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, remaining);
    }

    [Fact]
    public void Log_IncludesExceptionDetails()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));
        using (var logger = new FileLogger(_temp.Path, timeProvider: clock))
        {
            logger.Error("失败", new InvalidOperationException("boom"));
        }

        var text = File.ReadAllText(Path.Combine(_temp.Path, "client-20260925.log"));
        Assert.Contains("[ERROR] 失败", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_AfterDispose_DoesNotThrowOrWrite()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));
        var logger = new FileLogger(_temp.Path, timeProvider: clock);
        logger.Dispose();

        logger.Info("ignored");

        Assert.False(File.Exists(Path.Combine(_temp.Path, "client-20260925.log")));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveRetention()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileLogger(_temp.Path, retentionDays: 0));
    }
}
