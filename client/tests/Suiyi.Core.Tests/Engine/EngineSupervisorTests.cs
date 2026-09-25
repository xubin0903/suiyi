using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

public sealed class EngineSupervisorTests : IAsyncDisposable
{
    private static readonly EngineCommand Command = new()
    {
        FileName = "python",
        Arguments = ["-m", "suiyi_engine", "serve", "--port", "18780", "--preload", "zh-en,en-zh"],
        WorkingDirectory = "/repo",
        Source = EngineCommandSource.RepositoryVenv,
    };

    private readonly ObservableTimeProvider _time = new();
    private readonly FakeLauncher _launcher = new();
    private readonly FakeEndpoint _endpoint = new();
    private readonly ListLogger _outputLog = new();
    private readonly ListLogger _logger = new();
    private readonly List<EngineStateChangedEventArgs> _events = [];
    private readonly EngineSupervisor _supervisor;

    public EngineSupervisorTests()
    {
        _supervisor = new EngineSupervisor(
            new EngineOptions(),
            _launcher,
            _endpoint,
            () => Command,
            _time,
            logger: _logger,
            outputLogger: _outputLog);
        _supervisor.StateChanged += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
    }

    public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync();

    private IReadOnlyList<EngineStateChangedEventArgs> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    private IEnumerable<(EngineState, EngineOwnership)> Sequence => Events.Select(e => (e.State, e.Ownership));

    private Task<EngineStateChangedEventArgs> WaitForState(EngineState state)
    {
        var tcs = new TaskCompletionSource<EngineStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, EngineStateChangedEventArgs e)
        {
            if (e.State == state)
            {
                tcs.TrySetResult(e);
            }
        }

        _supervisor.StateChanged += Handler;
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(
            t =>
            {
                _supervisor.StateChanged -= Handler;
                return t.Result;
            },
            TaskScheduler.Default);
    }

    /// <summary>启动并在第一次探测就绪时进入 Ready(Managed)，循环停在看门狗计时器上。</summary>
    private async Task StartReadyAsync()
    {
        _endpoint.Script(false); // 没有外部服务
        _endpoint.Healthy = true;
        var parked = _time.NextTimer();
        _supervisor.Start();
        await parked;
        Assert.Equal(EngineState.Ready, _supervisor.State);
        Assert.Equal(EngineOwnership.Managed, _supervisor.Ownership);
    }

    /// <summary>让当前进程崩溃，等待进入 Restarting 并停在退避计时器上。</summary>
    private async Task<EngineStateChangedEventArgs> CrashAsync(int exitCode = 1)
    {
        var restarting = WaitForState(EngineState.Restarting);
        var parked = _time.NextTimer();
        _launcher.Last.Exit(exitCode);
        var e = await restarting;
        await parked;
        return e;
    }

    // ---- 请求健康检查（#34） ----

    [Fact]
    public async Task RequestHealthCheck_ProbesImmediatelyWithoutWaitingForInterval()
    {
        await StartReadyAsync();
        var probes = _endpoint.Probes;

        var parked = _time.NextTimer();
        _supervisor.RequestHealthCheck();
        await parked;

        Assert.Equal(probes + 1, _endpoint.Probes);
        Assert.Equal(EngineState.Ready, _supervisor.State);
    }

    [Fact]
    public async Task RequestHealthCheck_ExternalService_CountsTowardFailureThreshold()
    {
        _endpoint.Healthy = true;
        var parked = _time.NextTimer();
        _supervisor.Start();
        await parked;

        _endpoint.Healthy = false;
        for (var i = 0; i < 3; i++)
        {
            parked = _time.NextTimer();
            _supervisor.RequestHealthCheck();
            await parked;
        }

        Assert.Single(_launcher.Started); // 连续三次失败后自己拉起服务
    }

    [Fact]
    public void RequestHealthCheck_BeforeStart_IsHarmless() => _supervisor.RequestHealthCheck();

    // ---- 外部服务 ----

    [Fact]
    public async Task Start_ExternalServiceHealthy_ReusesWithoutLaunching()
    {
        _endpoint.Healthy = true;
        var parked = _time.NextTimer();

        _supervisor.Start();
        await parked;

        Assert.Equal([(EngineState.Starting, EngineOwnership.None), (EngineState.Ready, EngineOwnership.External)], Sequence);
        Assert.Empty(_launcher.Commands);

        await _supervisor.StopAsync();
        Assert.Equal(EngineState.Stopped, _supervisor.State);
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task External_ThreeFailedHealthChecks_LaunchesOwnService()
    {
        _endpoint.Healthy = true;
        var parked = _time.NextTimer();
        _supervisor.Start();
        await parked;

        _endpoint.Script(false, false, false); // 三次看门狗失败
        _endpoint.Healthy = true; // 之后自己拉起的服务健康
        await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(10));
        await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(_launcher.Started);
        await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(10));

        Assert.Single(_launcher.Started);
        Assert.Equal((EngineState.Ready, EngineOwnership.Managed), Sequence.Last());
    }

    // ---- 正常就绪 ----

    [Fact]
    public async Task Start_LaunchesAndBecomesReadyWhenHealthOk()
    {
        _endpoint.Script(false, false, false); // 外部检查 + 两次启动探测
        _endpoint.Healthy = true;
        var parked = _time.NextTimer();
        _supervisor.Start();
        await parked;

        Assert.Equal(EngineState.Starting, _supervisor.State);
        Assert.Same(Command, Assert.Single(_launcher.Commands));

        await _time.AdvanceAndSettleAsync(TimeSpan.FromMilliseconds(200));
        Assert.Equal(EngineState.Starting, _supervisor.State);
        await _time.AdvanceAndSettleAsync(TimeSpan.FromMilliseconds(200));

        Assert.Equal(
            [(EngineState.Starting, EngineOwnership.None), (EngineState.Starting, EngineOwnership.Managed), (EngineState.Ready, EngineOwnership.Managed)],
            Sequence);
        Assert.Contains("400 ms", Events[^1].Detail, StringComparison.Ordinal);
        Assert.Equal(_launcher.Last.Id, _supervisor.ProcessId);
        Assert.Same(Command, _supervisor.LastCommand);
    }

    [Fact]
    public async Task Ready_SendsOneWarmUp()
    {
        await StartReadyAsync();

        await Task.Run(() => SpinWait.SpinUntil(() => _endpoint.WarmUps > 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _endpoint.WarmUps);
    }

    [Fact]
    public async Task Output_IsLoggedAndTailKeepsLast50Lines()
    {
        _endpoint.Script(false);
        _endpoint.Healthy = true;
        _launcher.Enqueue(null, [.. Enumerable.Range(1, 60).Select(i => $"line {i}")]);
        var parked = _time.NextTimer();
        _supervisor.Start();
        await parked;

        Assert.Equal(60, _outputLog.Lines.Count);
        Assert.Equal("[stderr] line 60", _outputLog.Lines[^1]);
        Assert.Equal(50, _supervisor.RecentOutput.Count);
        Assert.Equal("line 11", _supervisor.RecentOutput[0]);
    }

    // ---- 启动超时 ----

    [Fact]
    public async Task Start_NotHealthyWithin30Seconds_KillsAndFails()
    {
        _endpoint.Healthy = false;
        var parked = _time.NextTimer();
        _supervisor.Start();
        await parked;

        for (var i = 0; i < 149; i++)
        {
            await _time.AdvanceAndSettleAsync(TimeSpan.FromMilliseconds(200));
        }

        Assert.Equal(EngineState.Starting, _supervisor.State);
        Assert.Equal(0, _launcher.Last.KillCount);

        var failed = WaitForState(EngineState.Failed);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        var e = await failed;

        Assert.Equal(EngineFailureReason.StartupTimeout, e.Failure!.Reason);
        Assert.Contains("启动超时", e.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, _launcher.Last.KillCount);
        Assert.Single(_launcher.Started);
    }

    // ---- 就绪前退出：不重试，提示正确 ----

    [Theory]
    [InlineData("/usr/bin/python: No module named suiyi_engine", EngineFailureHints.ModuleNotFound)]
    [InlineData("不支持的语向 fr→de，未下载模型：opus-mt-fr-en、opus-mt-en-de", "缺少模型：opus-mt-fr-en、opus-mt-en-de，请按 docs/engine/模型目录约定.md 转换")]
    [InlineData("端口 18780 已被占用或无法在 127.0.0.1 上监听：[Errno 98] Address already in use", "端口 18780 被其他程序占用，请在设置中修改 engine.port")]
    public async Task ExitBeforeReady_FailsWithHintAndDoesNotRetry(string stderr, string expected)
    {
        _endpoint.Healthy = false;
        _launcher.Enqueue(1, stderr);
        var failed = WaitForState(EngineState.Failed);

        _supervisor.Start();
        var e = await failed;

        Assert.Equal(EngineFailureReason.ExitedBeforeReady, e.Failure!.Reason);
        Assert.Equal(expected, e.Failure.Message);
        Assert.Equal(e.Failure, _supervisor.Failure);

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Single(_launcher.Commands);
        Assert.Equal(EngineState.Failed, _supervisor.State);
    }

    [Fact]
    public async Task ExitBeforeReady_PortTakenBySuiyi_ReusesExternal()
    {
        _endpoint.Script(false); // 启动前检查时还没有
        _endpoint.Healthy = true; // 退出后再查：端口上是随译服务
        _launcher.Enqueue(1, "端口 18780 已被占用或无法在 127.0.0.1 上监听：[Errno 98] Address already in use");
        var ready = WaitForState(EngineState.Ready);

        _supervisor.Start();
        var e = await ready;

        Assert.Equal(EngineOwnership.External, e.Ownership);
        Assert.Single(_launcher.Commands);
    }

    [Fact]
    public async Task LaunchFailure_FailsWithPythonHint()
    {
        _endpoint.Healthy = false;
        _launcher.EnqueueLaunchFailure();
        var failed = WaitForState(EngineState.Failed);

        _supervisor.Start();
        var e = await failed;

        Assert.Equal(EngineFailureReason.LaunchFailed, e.Failure!.Reason);
        Assert.StartsWith("未找到 Python", e.Failure.Message, StringComparison.Ordinal);
    }

    // ---- 崩溃重启 ----

    [Fact]
    public async Task Crash_RestartsWithBackoff1s2s4s()
    {
        await StartReadyAsync();

        foreach (var (attempt, backoff) in new[] { (1, 1000), (2, 2000), (3, 4000) })
        {
            var e = await CrashAsync();
            Assert.Equal(EngineOwnership.Managed, e.Ownership);
            Assert.Contains($"{backoff / 1000} 秒后第 {attempt} 次重启", e.Detail, StringComparison.Ordinal);
            Assert.Contains("退出码 1", e.Detail, StringComparison.Ordinal);

            _time.Advance(TimeSpan.FromMilliseconds(backoff - 1));
            Assert.Equal(attempt, _launcher.Started.Count);

            await _time.AdvanceAndSettleAsync(TimeSpan.FromMilliseconds(1));
            Assert.Equal(attempt + 1, _launcher.Started.Count);
            Assert.Equal(EngineState.Ready, _supervisor.State);
        }

        Assert.Equal(
            [EngineState.Starting, EngineState.Starting, EngineState.Ready,
             EngineState.Restarting, EngineState.Ready,
             EngineState.Restarting, EngineState.Ready,
             EngineState.Restarting, EngineState.Ready],
            Events.Select(e => e.State));
    }

    [Fact]
    public async Task Crash_FourthWithinFiveMinutes_FailsWithoutRestart()
    {
        await StartReadyAsync();
        foreach (var backoff in new[] { 1, 2, 4 })
        {
            await CrashAsync();
            await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(backoff));
        }

        var failed = WaitForState(EngineState.Failed);
        _launcher.Last.Exit(1);
        var e = await failed;

        Assert.Equal(EngineFailureReason.CrashedRepeatedly, e.Failure!.Reason);
        Assert.Contains("服务反复崩溃", e.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(4, _launcher.Started.Count);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(4, _launcher.Started.Count);
    }

    [Fact]
    public async Task Crash_OldCrashesOutsideWindow_DoNotCount()
    {
        await StartReadyAsync();
        foreach (var backoff in new[] { 1, 2, 4 })
        {
            await CrashAsync();
            await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(backoff));
        }

        // 健康运行 5 分钟多（看门狗每 10 秒探测一次，都成功）。
        for (var i = 0; i < 31; i++)
        {
            await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(10));
        }

        var e = await CrashAsync();
        Assert.Equal(EngineState.Restarting, e.State);
        Assert.Contains("1 秒后第 1 次重启", e.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Crash_RestartHitsPortStillInUse_KeepsBackingOff()
    {
        await StartReadyAsync();
        await CrashAsync();

        _launcher.Enqueue(1, "端口 18780 已被占用或无法在 127.0.0.1 上监听：[Errno 98] Address already in use");
        _endpoint.Script(false); // 端口上不是随译
        var restarting = WaitForState(EngineState.Restarting);
        var parked = _time.NextTimer();
        _time.Advance(TimeSpan.FromSeconds(1));
        var e = await restarting;
        await parked;

        Assert.Contains("端口仍被上一个服务进程的连接占用", e.Detail, StringComparison.Ordinal);
        Assert.Contains("2 秒后第 2 次重启", e.Detail, StringComparison.Ordinal);

        await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(EngineState.Ready, _supervisor.State);
        Assert.Equal(3, _launcher.Started.Count);
    }

    [Fact]
    public async Task Restart_ResetsCrashCounter()
    {
        await StartReadyAsync();
        foreach (var backoff in new[] { 1, 2, 4 })
        {
            await CrashAsync();
            await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(backoff));
        }

        var current = _launcher.Last;
        _endpoint.Script(false); // 重启后的外部检查：端口上没有别的服务
        var ready = WaitForState(EngineState.Ready);
        await _supervisor.RestartAsync();
        await ready;
        await Task.Run(() => SpinWait.SpinUntil(() => _launcher.Started.Count == 5, TimeSpan.FromSeconds(5)));

        Assert.Equal(1, current.KillCount);
        Assert.Equal(5, _launcher.Started.Count);
        Assert.DoesNotContain(Events, e => e.State == EngineState.Stopped);

        var e = await CrashAsync();
        Assert.Contains("第 1 次重启", e.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_FromFailed_StartsAgain()
    {
        _endpoint.Healthy = false;
        _launcher.Enqueue(1, "No module named suiyi_engine");
        var failed = WaitForState(EngineState.Failed);
        _supervisor.Start();
        await failed;

        _endpoint.Healthy = true;
        var ready = WaitForState(EngineState.Ready);
        await _supervisor.RestartAsync();
        var e = await ready;

        Assert.Equal(EngineOwnership.External, e.Ownership);
    }

    [Fact]
    public async Task Restart_RepeatedWhileInProgress_RestartsOnce()
    {
        await StartReadyAsync();
        var before = _launcher.Started.Count;
        _endpoint.Script(false); // 重启后的外部检查：端口上没有别的服务
        _endpoint.Healthy = false; // 新进程迟迟不就绪：重启一直「进行中」

        await _supervisor.RestartAsync();
        await Task.Run(() => SpinWait.SpinUntil(() => _launcher.Started.Count == before + 1, TimeSpan.FromSeconds(5)));
        Assert.True(_supervisor.IsRestarting);
        Assert.Equal(EngineState.Starting, _supervisor.State);

        await _supervisor.RestartAsync(); // 浮窗「重试」
        await _supervisor.RestartAsync(); // 托盘「重启翻译服务」

        Assert.Equal(before + 1, _launcher.Started.Count);
        Assert.Contains(_logger.Lines, l => l.Contains("重启已在进行", StringComparison.Ordinal));

        var ready = WaitForState(EngineState.Ready);
        _endpoint.Healthy = true;
        await _time.AdvanceAndSettleAsync(TimeSpan.FromMilliseconds(200));
        await ready;
        Assert.False(_supervisor.IsRestarting);
    }

    [Fact]
    public async Task Restart_ConcurrentCalls_DoNotStack()
    {
        await StartReadyAsync();
        var before = _launcher.Started.Count;
        _endpoint.Script(false);
        _endpoint.Healthy = false; // 推进时钟前不会就绪，保证所有调用都落在同一次重启里

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(_supervisor.RestartAsync)));
        await Task.Run(() => SpinWait.SpinUntil(() => _launcher.Started.Count == before + 1, TimeSpan.FromSeconds(5)));

        var ready = WaitForState(EngineState.Ready);
        _endpoint.Healthy = true;
        await _time.AdvanceAndSettleAsync(TimeSpan.FromMilliseconds(200));
        await ready;

        Assert.Equal(before + 1, _launcher.Started.Count);
        Assert.False(_supervisor.IsRestarting);
    }

    [Fact]
    public async Task Restart_AfterSettled_CanRestartAgain()
    {
        await StartReadyAsync();
        var before = _launcher.Started.Count;

        for (var i = 0; i < 2; i++)
        {
            _endpoint.Script(false);
            var ready = WaitForState(EngineState.Ready);
            await _supervisor.RestartAsync();
            await ready;
        }

        Assert.Equal(before + 2, _launcher.Started.Count);
    }

    [Fact]
    public async Task Restart_ThenStop_ClearsInProgress()
    {
        await StartReadyAsync();
        _endpoint.Script(false);
        _endpoint.Healthy = false;
        await _supervisor.RestartAsync();
        Assert.True(_supervisor.IsRestarting);

        await _supervisor.StopAsync();

        Assert.False(_supervisor.IsRestarting);
    }

    // ---- 看门狗 ----

    [Fact]
    public async Task Watchdog_ThreeConsecutiveFailures_KillsAndTreatsAsCrash()
    {
        await StartReadyAsync();
        var process = _launcher.Last;
        _endpoint.Healthy = false;

        await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(10));
        await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EngineState.Ready, _supervisor.State);
        Assert.Equal(0, process.KillCount);

        var restarting = WaitForState(EngineState.Restarting);
        _time.Advance(TimeSpan.FromSeconds(10));
        var e = await restarting;

        Assert.Equal(1, process.KillCount);
        Assert.Contains("健康检查连续 3 次失败", e.Detail, StringComparison.Ordinal);
        Assert.Contains("1 秒后第 1 次重启", e.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watchdog_SuccessResetsFailureCount()
    {
        await StartReadyAsync();
        _endpoint.Script(false, false, true, false, false);
        _endpoint.Healthy = true;

        for (var i = 0; i < 6; i++)
        {
            await _time.AdvanceAndSettleAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(EngineState.Ready, _supervisor.State);
        Assert.Equal(0, _launcher.Last.KillCount);
    }

    // ---- 停止 ----

    [Fact]
    public async Task Stop_KillsManagedProcessAndIsNotACrash()
    {
        await StartReadyAsync();
        var process = _launcher.Last;

        await _supervisor.StopAsync();

        Assert.Equal(1, process.KillCount);
        Assert.True(process.Disposed);
        Assert.Equal(EngineState.Stopped, _supervisor.State);
        Assert.DoesNotContain(Events, e => e.State == EngineState.Restarting);
        Assert.Null(_supervisor.ProcessId);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(_launcher.Started);
    }

    [Fact]
    public async Task Stop_ProcessDoesNotExit_GivesUpAfterTimeout()
    {
        await StartReadyAsync();
        _launcher.Last.ExitOnKill = false;

        var parked = _time.NextTimer();
        var stop = _supervisor.StopAsync();
        await parked; // 2 秒等待计时器
        Assert.False(stop.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(2));
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EngineState.Stopped, _supervisor.State);
    }

    [Fact]
    public async Task Start_WhileRunning_IsIgnored()
    {
        await StartReadyAsync();

        _supervisor.Start();

        Assert.Single(_launcher.Started);
    }
}
