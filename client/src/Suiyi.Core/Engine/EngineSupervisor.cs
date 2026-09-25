using System.Globalization;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Engine;

/// <summary>
/// 翻译服务进程的监管器：复用外部服务、拉起、就绪探测、崩溃退避重启、看门狗、退出清理。
/// </summary>
/// <remarks>
/// <para>状态：<c>Stopped → Starting → Ready</c>；托管服务崩溃时 <c>Ready → Restarting → Ready</c>；
/// 不可恢复时 <c>→ Failed</c>。状态变化通过 <see cref="StateChanged"/> 通知，
/// <b>事件在线程池线程上触发</b>，界面需自行切回 UI 线程。</para>
/// <para>进程启动器、HTTP 探测、时钟均通过构造参数注入，单测用假实现与 FakeTimeProvider。</para>
/// </remarks>
public sealed class EngineSupervisor : IEngineStatus, IAsyncDisposable
{
    private readonly EngineOptions _options;
    private readonly IEngineProcessLauncher _launcher;
    private readonly IEngineEndpoint _endpoint;
    private readonly Func<EngineCommand> _commandFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IAppLogger _logger;
    private readonly IAppLogger? _outputLogger;
    private readonly OutputTail _tail;
    private readonly object _gate = new();
    private readonly List<DateTimeOffset> _crashes = [];

    private CancellationTokenSource? _session;
    private Task _loop = Task.CompletedTask;
    private IEngineProcess? _process;
    private EngineState _state = EngineState.Stopped;
    private EngineOwnership _ownership = EngineOwnership.None;
    private EngineFailure? _failure;
    private EngineCommand? _lastCommand;
    private TaskCompletionSource _wakeUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _restarting;
    private bool _restartArmed;

    /// <summary>创建监管器。</summary>
    /// <param name="options">配置。</param>
    /// <param name="launcher">进程启动器，生产环境为 <see cref="ProcessEngineLauncher"/>。</param>
    /// <param name="endpoint">HTTP 探测，生产环境为 <see cref="EngineClientEndpoint"/>。</param>
    /// <param name="commandFactory">每次拉起前解析命令；默认 <see cref="EngineCommandResolver.Resolve"/> + 当前环境。</param>
    /// <param name="timeProvider">时钟；默认 <see cref="TimeProvider.System"/>。</param>
    /// <param name="logger">客户端日志（状态变化、失败原因）。</param>
    /// <param name="outputLogger">服务 stdout / stderr 的去向（<c>engine-YYYYMMDD.log</c>）。</param>
    public EngineSupervisor(
        EngineOptions options,
        IEngineProcessLauncher launcher,
        IEngineEndpoint endpoint,
        Func<EngineCommand>? commandFactory = null,
        TimeProvider? timeProvider = null,
        IAppLogger? logger = null,
        IAppLogger? outputLogger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(endpoint);
        _options = options;
        _launcher = launcher;
        _endpoint = endpoint;
        _commandFactory = commandFactory ?? (() => EngineCommandResolver.Resolve(options, EngineCommandEnvironment.Current()));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullAppLogger.Instance;
        _outputLogger = outputLogger;
        _tail = new OutputTail(options.OutputTailLines);
    }

    /// <summary>状态变化（线程池线程上触发）。</summary>
    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    /// <summary>当前状态。</summary>
    public EngineState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>当前服务归属。</summary>
    public EngineOwnership Ownership
    {
        get
        {
            lock (_gate)
            {
                return _ownership;
            }
        }
    }

    /// <summary>最近一次失败；未失败时为 <see langword="null"/>。</summary>
    public EngineFailure? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    /// <summary>最近一次拉起使用的命令。</summary>
    public EngineCommand? LastCommand
    {
        get
        {
            lock (_gate)
            {
                return _lastCommand;
            }
        }
    }

    /// <summary>托管进程的 id；没有托管进程时为 <see langword="null"/>。</summary>
    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _process?.Id;
            }
        }
    }

    /// <summary>服务最近的输出（最多 <see cref="EngineOptions.OutputTailLines"/> 行）。</summary>
    public IReadOnlyList<string> RecentOutput => _tail.Snapshot();

    /// <summary>开始监管（立即返回）。已在运行时不做任何事；<see cref="EngineState.Failed"/> 后可再次调用。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_session is not null && !_loop.IsCompleted)
            {
                return;
            }

            _session?.Dispose();
            _session = new CancellationTokenSource();
            var token = _session.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    /// <summary>
    /// 重启服务（托盘「重启翻译服务」、浮窗「重试」）：结束托管进程、清零崩溃计数后重新开始。外部服务不会被结束。
    /// 一次重启从调用开始，到服务再次进入 Ready / Failed / Stopped 为止；期间重复调用被忽略，不会叠加。
    /// </summary>
    public Task RestartAsync()
    {
        if (Interlocked.CompareExchange(ref _restarting, 1, 0) != 0)
        {
            _logger.Info("翻译服务：重启已在进行，忽略重复请求");
            return Task.CompletedTask;
        }

        return RestartCoreAsync();
    }

    /// <summary>是否有重启在进行（见 <see cref="RestartAsync"/>）。</summary>
    public bool IsRestarting => Volatile.Read(ref _restarting) != 0;

    private async Task RestartCoreAsync()
    {
        try
        {
            _logger.Info("翻译服务：手动重启");
            await StopCoreAsync(raiseStopped: false).ConfigureAwait(false);
            lock (_gate)
            {
                _crashes.Clear();
            }

            Volatile.Write(ref _restartArmed, true);
            Start();
        }
        catch
        {
            EndRestart();
            throw;
        }
    }

    private void EndRestart()
    {
        Volatile.Write(ref _restartArmed, false);
        Volatile.Write(ref _restarting, 0);
    }

    /// <summary>
    /// 停止监管。托管进程用 <c>Kill(entireProcessTree: true)</c> 结束（服务没有优雅关闭接口），
    /// 最多等待 <see cref="EngineOptions.StopTimeout"/>；外部服务不动。
    /// </summary>
    public Task StopAsync() => StopCoreAsync(raiseStopped: true);

    /// <summary>请求看门狗立即做一次健康检查（不等 <see cref="EngineOptions.WatchdogInterval"/>）。未就绪时无效果。</summary>
    public void RequestHealthCheck()
    {
        var previous = Interlocked.Exchange(ref _wakeUp, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    private async Task StopCoreAsync(bool raiseStopped)
    {
        Task loop;
        lock (_gate)
        {
            _session?.Cancel();
            loop = _loop;
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期。
        }

        IEngineProcess? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            await KillAndWaitAsync(process, "停止").ConfigureAwait(false);
        }

        if (raiseStopped && State != EngineState.Stopped)
        {
            SetState(EngineState.Stopped, EngineOwnership.None, "翻译服务已停止");
        }

        if (raiseStopped)
        {
            EndRestart();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RunSessionAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop / Restart。
        }
        catch (Exception ex)
        {
            _logger.Error("翻译服务监管异常", ex);
            await ReleaseProcessAsync(kill: true).ConfigureAwait(false);
            SetFailed(EngineFailureReason.LaunchFailed, "翻译服务监管出错：" + ex.Message);
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        SetState(EngineState.Starting, EngineOwnership.None, Invariant($"检查端口 {_options.Port} 上是否已有翻译服务"));
        var checkExternal = true;
        var restarting = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (checkExternal && await _endpoint.IsHealthyAsync(ct).ConfigureAwait(false))
            {
                await RunExternalAsync(ct).ConfigureAwait(false);
                SetState(EngineState.Starting, EngineOwnership.None, "外部翻译服务已无响应，改为自行启动");
                restarting = false;
            }

            checkExternal = false;
            var (outcome, reason) = await LaunchAndWaitReadyAsync(restarting, ct).ConfigureAwait(false);
            if (outcome == LaunchOutcome.Failed)
            {
                return;
            }

            if (outcome == LaunchOutcome.External)
            {
                checkExternal = true;
                continue;
            }

            if (outcome == LaunchOutcome.Ready)
            {
                StartWarmUp(ct);
                reason = await MonitorManagedAsync(ct).ConfigureAwait(false);
            }

            var now = _timeProvider.GetUtcNow();
            int count;
            lock (_gate)
            {
                _crashes.RemoveAll(t => now - t > _options.CrashWindow);
                _crashes.Add(now);
                count = _crashes.Count;
            }

            if (count > _options.MaxCrashesInWindow)
            {
                SetFailed(
                    EngineFailureReason.CrashedRepeatedly,
                    Invariant($"服务反复崩溃（{_options.CrashWindow.TotalMinutes:0} 分钟内 {count} 次），已停止自动重启。最后一次：{reason}"));
                return;
            }

            var backoff = _options.RestartBackoff.Count == 0
                ? TimeSpan.Zero
                : _options.RestartBackoff[Math.Min(count, _options.RestartBackoff.Count) - 1];
            SetState(
                EngineState.Restarting,
                EngineOwnership.Managed,
                Invariant($"{reason}，{backoff.TotalSeconds:0.#} 秒后第 {count} 次重启"));
            await Task.Delay(backoff, _timeProvider, ct).ConfigureAwait(false);
            restarting = true;
        }
    }

    private async Task RunExternalAsync(CancellationToken ct)
    {
        SetState(EngineState.Ready, EngineOwnership.External, Invariant($"复用已运行的翻译服务（端口 {_options.Port}），退出时不结束它"));
        StartWarmUp(ct);
        var failures = 0;
        while (failures < _options.WatchdogFailureThreshold)
        {
            await WatchdogDelayAsync(null, ct).ConfigureAwait(false);
            if (await _endpoint.IsHealthyAsync(ct).ConfigureAwait(false))
            {
                failures = 0;
            }
            else
            {
                failures++;
                _logger.Warn(Invariant($"翻译服务：外部服务健康检查失败（连续 {failures} 次）"));
            }
        }
    }

    private async Task<(LaunchOutcome Outcome, string Reason)> LaunchAndWaitReadyAsync(bool restarting, CancellationToken ct)
    {
        _tail.Clear();
        var command = _commandFactory();
        lock (_gate)
        {
            _lastCommand = command;
        }

        _logger.Info(Invariant($"翻译服务：启动 {command}（来源 {command.Source}，工作目录 {command.WorkingDirectory}）"));
        if (!restarting)
        {
            SetState(EngineState.Starting, EngineOwnership.Managed, "正在启动翻译服务");
        }

        IEngineProcess process;
        try
        {
            process = _launcher.Start(command, OnOutput);
        }
        catch (EngineLaunchException ex)
        {
            _logger.Error("翻译服务：无法启动进程", ex);
            SetFailed(EngineFailureReason.LaunchFailed, EngineFailureHints.ForLaunchFailure(command));
            return (LaunchOutcome.Failed, string.Empty);
        }

        lock (_gate)
        {
            _process = process;
        }

        var started = _timeProvider.GetTimestamp();
        var exitTask = process.WaitForExitAsync(ct);
        while (true)
        {
            if (exitTask.IsCompleted || process.HasExited)
            {
                await exitTask.ConfigureAwait(false);
                return await HandleEarlyExitAsync(process, restarting, ct).ConfigureAwait(false);
            }

            if (await _endpoint.IsHealthyAsync(ct).ConfigureAwait(false) && !process.HasExited)
            {
                var elapsed = _timeProvider.GetElapsedTime(started);
                SetState(
                    EngineState.Ready,
                    EngineOwnership.Managed,
                    Invariant($"翻译服务已就绪（pid {process.Id}，用时 {elapsed.TotalMilliseconds:0} ms）"));
                return (LaunchOutcome.Ready, string.Empty);
            }

            if (_timeProvider.GetElapsedTime(started) >= _options.StartupTimeout)
            {
                await ReleaseProcessAsync(kill: true).ConfigureAwait(false);
                SetFailed(
                    EngineFailureReason.StartupTimeout,
                    Invariant($"启动超时：翻译服务 {_options.StartupTimeout.TotalSeconds:0} 秒内未就绪，已结束进程"));
                return (LaunchOutcome.Failed, string.Empty);
            }

            await Task.WhenAny(exitTask, Task.Delay(_options.StartupProbeInterval, _timeProvider, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    private async Task<(LaunchOutcome Outcome, string Reason)> HandleEarlyExitAsync(IEngineProcess process, bool restarting, CancellationToken ct)
    {
        var exitCode = process.ExitCode;
        var output = _tail.Snapshot();
        await ReleaseProcessAsync(kill: false).ConfigureAwait(false);
        _logger.Warn(Invariant($"翻译服务：就绪前退出，退出码 {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}"));

        var portInUse = EngineFailureHints.IsPortInUse(output);
        if (portInUse && await _endpoint.IsHealthyAsync(ct).ConfigureAwait(false))
        {
            // 端口上其实是随译服务（例如刚被手动启动），改为复用。
            return (LaunchOutcome.External, string.Empty);
        }

        if (portInUse && restarting)
        {
            // 崩溃重启时，上一个进程遗留的连接可能还占着端口一小会儿：按又一次崩溃处理，继续退避，受 5 分钟次数上限约束。
            return (LaunchOutcome.Crashed, "端口仍被上一个服务进程的连接占用");
        }

        SetFailed(EngineFailureReason.ExitedBeforeReady, EngineFailureHints.ForEarlyExit(output, _options.Port, exitCode));
        return (LaunchOutcome.Failed, string.Empty);
    }

    private async Task<string> MonitorManagedAsync(CancellationToken ct)
    {
        IEngineProcess process;
        lock (_gate)
        {
            process = _process ?? throw new InvalidOperationException("没有托管进程");
        }

        var exitTask = process.WaitForExitAsync(ct);
        var failures = 0;
        while (true)
        {
            await WatchdogDelayAsync(exitTask, ct).ConfigureAwait(false);
            if (exitTask.IsCompleted || process.HasExited)
            {
                var code = process.ExitCode;
                await ReleaseProcessAsync(kill: false).ConfigureAwait(false);
                return Invariant($"服务意外退出（退出码 {code?.ToString(CultureInfo.InvariantCulture) ?? "?"}）");
            }

            var healthy = await _endpoint.IsHealthyAsync(ct).ConfigureAwait(false);
            if (process.HasExited)
            {
                continue;
            }

            if (healthy)
            {
                failures = 0;
                continue;
            }

            failures++;
            _logger.Warn(Invariant($"翻译服务：健康检查失败（连续 {failures} 次）"));
            if (failures >= _options.WatchdogFailureThreshold)
            {
                await ReleaseProcessAsync(kill: true).ConfigureAwait(false);
                return Invariant($"健康检查连续 {failures} 次失败，已结束无响应的服务");
            }
        }
    }

    /// <summary>等待看门狗间隔，或进程退出，或 <see cref="RequestHealthCheck"/>，先到者为准。</summary>
    private async Task WatchdogDelayAsync(Task? exitTask, CancellationToken ct)
    {
        var wake = Volatile.Read(ref _wakeUp).Task;
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(_options.WatchdogInterval, _timeProvider, delayCts.Token);
        await (exitTask is null ? Task.WhenAny(delay, wake) : Task.WhenAny(delay, wake, exitTask)).ConfigureAwait(false);
        delayCts.Cancel();
        ct.ThrowIfCancellationRequested();
    }

    private void StartWarmUp(CancellationToken ct)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _endpoint.WarmUpAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 停止时取消。
                }
                catch (Exception ex)
                {
                    _logger.Warn("翻译服务：预热请求失败（不影响状态）", ex);
                }
            },
            CancellationToken.None);
    }

    private async Task ReleaseProcessAsync(bool kill)
    {
        IEngineProcess? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        if (kill)
        {
            await KillAndWaitAsync(process, "结束").ConfigureAwait(false);
        }
        else
        {
            process.Dispose();
        }
    }

    private async Task KillAndWaitAsync(IEngineProcess process, string action)
    {
        var id = process.Id;
        try
        {
            process.Kill();
            using var timeout = new CancellationTokenSource(_options.StopTimeout, _timeProvider);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                _logger.Info(Invariant($"翻译服务：已{action}进程 {id}"));
            }
            catch (OperationCanceledException)
            {
                _logger.Warn(Invariant($"翻译服务：{action}进程 {id} 后 {_options.StopTimeout.TotalSeconds:0} 秒内未确认退出"));
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OnOutput(EngineOutputLine line)
    {
        _tail.Add(line.Text);
        _outputLogger?.Log(LogLevel.Info, line.IsError ? "[stderr] " + line.Text : line.Text);
    }

    private void SetFailed(EngineFailureReason reason, string message)
    {
        _logger.Error("翻译服务失败：" + message);
        SetState(EngineState.Failed, EngineOwnership.None, message, new EngineFailure(reason, message));
    }

    private void SetState(EngineState state, EngineOwnership ownership, string detail, EngineFailure? failure = null)
    {
        var args = new EngineStateChangedEventArgs(state, ownership, detail, failure);
        lock (_gate)
        {
            _state = state;
            _ownership = ownership;
            if (state is EngineState.Failed || failure is not null)
            {
                _failure = failure;
            }
            else if (state is EngineState.Ready)
            {
                _failure = null;
            }
        }

        if ((state is EngineState.Ready or EngineState.Failed or EngineState.Stopped) && Volatile.Read(ref _restartArmed))
        {
            EndRestart();
        }

        _logger.Info("翻译服务状态：" + args);
        try
        {
            StateChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.Error("翻译服务状态事件处理出错", ex);
        }
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private enum LaunchOutcome
    {
        Ready,
        External,
        Crashed,
        Failed,
    }
}
