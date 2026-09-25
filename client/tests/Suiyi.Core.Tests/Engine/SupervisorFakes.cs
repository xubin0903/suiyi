using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Engine;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Tests.Engine;

/// <summary>记录计时器创建的 FakeTimeProvider：监管循环每次停在 <c>Task.Delay</c> 上都会创建计时器，测试据此判断循环已「停稳」。</summary>
internal sealed class ObservableTimeProvider : FakeTimeProvider
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>在执行动作之前取得，动作之后等待：下一次创建计时器时完成。</summary>
    public Task NextTimer() => Volatile.Read(ref _next).Task.WaitAsync(TimeSpan.FromSeconds(10));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return timer;
    }

    /// <summary>推进时间，并等待循环重新停在下一个计时器上。</summary>
    public async Task AdvanceAndSettleAsync(TimeSpan delta)
    {
        var next = NextTimer();
        Advance(delta);
        await next;
    }
}

internal sealed class FakeEngineProcess : IEngineProcess
{
    private static int _nextId = 1000;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id { get; } = Interlocked.Increment(ref _nextId);

    public bool HasExited => _exited.Task.IsCompleted;

    public int? ExitCode { get; private set; }

    public int KillCount { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>为 false 时模拟「结束后迟迟不退出」。</summary>
    public bool ExitOnKill { get; set; } = true;

    public void Exit(int code)
    {
        ExitCode = code;
        _exited.TrySetResult();
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);

    public void Kill()
    {
        KillCount++;
        if (ExitOnKill)
        {
            Exit(-1);
        }
    }

    public void Dispose() => Disposed = true;
}

/// <summary>按顺序应答的假启动器。</summary>
internal sealed class FakeLauncher : IEngineProcessLauncher
{
    private readonly ConcurrentQueue<Func<Action<EngineOutputLine>, IEngineProcess>> _plans = new();
    private readonly List<FakeEngineProcess> _started = [];
    private readonly List<EngineCommand> _commands = [];

    public IReadOnlyList<FakeEngineProcess> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    public IReadOnlyList<EngineCommand> Commands
    {
        get
        {
            lock (_started)
            {
                return [.. _commands];
            }
        }
    }

    public FakeEngineProcess Last => Started[^1];

    /// <summary>下一次启动：进程先输出 <paramref name="lines"/>（stderr），<paramref name="exitCode"/> 非空时立即退出。</summary>
    public void Enqueue(int? exitCode = null, params string[] lines) =>
        _plans.Enqueue(onOutput =>
        {
            var process = new FakeEngineProcess();
            foreach (var line in lines)
            {
                onOutput(new EngineOutputLine(line, IsError: true));
            }

            if (exitCode is { } code)
            {
                process.Exit(code);
            }

            return process;
        });

    public void EnqueueLaunchFailure() =>
        _plans.Enqueue(_ => throw new EngineLaunchException("找不到文件"));

    public IEngineProcess Start(EngineCommand command, Action<EngineOutputLine> onOutput)
    {
        var process = _plans.TryDequeue(out var plan) ? plan(onOutput) : new FakeEngineProcess();
        lock (_started)
        {
            _commands.Add(command);
            if (process is FakeEngineProcess fake)
            {
                _started.Add(fake);
            }
        }

        return process;
    }
}

internal sealed class FakeEndpoint : IEngineEndpoint
{
    private readonly ConcurrentQueue<bool> _scripted = new();
    private int _probes;
    private int _warmUps;

    /// <summary>脚本用完后的默认结果。</summary>
    public bool Healthy { get; set; }

    public int Probes => Volatile.Read(ref _probes);

    public int WarmUps => Volatile.Read(ref _warmUps);

    public void Script(params bool[] results)
    {
        foreach (var result in results)
        {
            _scripted.Enqueue(result);
        }
    }

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _probes);
        return Task.FromResult(_scripted.TryDequeue(out var result) ? result : Healthy);
    }

    public Task WarmUpAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _warmUps);
        return Task.CompletedTask;
    }
}

internal sealed class ListLogger : IAppLogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public void Log(LogLevel level, string message, Exception? exception = null) => _lines.Enqueue(message);
}
