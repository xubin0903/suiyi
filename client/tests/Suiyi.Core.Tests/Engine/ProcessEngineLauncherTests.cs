using System.Collections.Concurrent;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

/// <summary>用真实的系统命令验证 <see cref="ProcessEngineLauncher"/>（不监听端口）。</summary>
public class ProcessEngineLauncherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static EngineCommand Shell(string script) => OperatingSystem.IsWindows()
        ? new EngineCommand { FileName = "cmd.exe", Arguments = ["/d", "/c", script], WorkingDirectory = Path.GetTempPath(), Source = EngineCommandSource.ConfiguredCommand }
        : new EngineCommand { FileName = "/bin/sh", Arguments = ["-c", script], WorkingDirectory = Path.GetTempPath(), Source = EngineCommandSource.ConfiguredCommand };

    private static EngineCommand LongRunning() => OperatingSystem.IsWindows()
        ? new EngineCommand { FileName = "ping.exe", Arguments = ["-n", "60", "127.0.0.1"], WorkingDirectory = Path.GetTempPath(), Source = EngineCommandSource.ConfiguredCommand }
        : new EngineCommand { FileName = "sleep", Arguments = ["60"], WorkingDirectory = Path.GetTempPath(), Source = EngineCommandSource.ConfiguredCommand };

    [Fact]
    public async Task Start_CapturesStdoutStderrAndExitCode()
    {
        var lines = new ConcurrentQueue<EngineOutputLine>();
        var script = OperatingSystem.IsWindows() ? "echo out& echo err 1>&2& exit /b 3" : "echo out; echo err 1>&2; exit 3";

        using var process = new ProcessEngineLauncher().Start(Shell(script), lines.Enqueue);
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.True(process.HasExited);
        Assert.Equal(3, process.ExitCode);
        Assert.Contains(lines, l => !l.IsError && l.Text.Trim() == "out");
        Assert.Contains(lines, l => l.IsError && l.Text.Trim() == "err");
    }

    [Fact]
    public async Task Start_PassesPythonEnvironmentForUtf8()
    {
        var lines = new ConcurrentQueue<EngineOutputLine>();
        var script = OperatingSystem.IsWindows() ? "echo %PYTHONUTF8%-%PYTHONUNBUFFERED%" : "echo $PYTHONUTF8-$PYTHONUNBUFFERED";

        using var process = new ProcessEngineLauncher().Start(Shell(script), lines.Enqueue);
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Contains(lines, l => l.Text.Trim() == "1-1");
    }

    [Fact]
    public async Task Kill_EndsLongRunningProcess()
    {
        using var process = new ProcessEngineLauncher().Start(LongRunning(), _ => { });
        Assert.False(process.HasExited);
        Assert.Null(process.ExitCode);

        process.Kill();
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.True(process.HasExited);
        process.Kill(); // 已退出时不抛异常
    }

    [Fact]
    public void Start_MissingExecutable_ThrowsLaunchException()
    {
        var command = new EngineCommand
        {
            FileName = Path.Combine(Path.GetTempPath(), "no-such-python-" + Guid.NewGuid().ToString("N")),
            Arguments = [],
            WorkingDirectory = Path.GetTempPath(),
            Source = EngineCommandSource.ConfiguredPython,
        };

        Assert.Throws<EngineLaunchException>(() => new ProcessEngineLauncher().Start(command, _ => { }));
    }

    [Fact]
    public void Start_AfterStartThrows_KillsProcessAndThrowsLaunchException()
    {
        System.Diagnostics.Process? started = null;
        var launcher = new ProcessEngineLauncher(p =>
        {
            started = p;
            throw new InvalidOperationException("job failed");
        });

        var ex = Assert.Throws<EngineLaunchException>(() => launcher.Start(LongRunning(), _ => { }));

        Assert.Contains("job failed", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(started);
    }
}
