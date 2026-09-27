using System.Globalization;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Engine;

/// <summary><c>/health.ocr_worker_state</c> 的取值（引擎 #104）。</summary>
public static class OcrWorkerStates
{
    /// <summary>没有 OCR 子进程（从没用过，或空闲退出、崩溃之后）：下一次 OCR 要冷启动。</summary>
    public const string Stopped = "stopped";

    /// <summary>子进程已启动、模型加载中。</summary>
    public const string Starting = "starting";

    /// <summary>模型已加载、空闲。</summary>
    public const string Ready = "ready";

    /// <summary>正在识别。</summary>
    public const string Busy = "busy";

    /// <summary>引擎用 <c>SUIYI_OCR_WORKER=0</c> 退回进程内 OCR（只用于排查问题）；是否已加载看 <c>ocr_loaded</c>。</summary>
    public const string InProcess = "in_process";
}

/// <summary><see cref="OcrPrewarmer.Decide"/> 的结论。</summary>
public enum OcrPrewarmDecision
{
    /// <summary>OCR 是冷的，发预热请求。</summary>
    Prewarm,

    /// <summary>老版引擎（<c>/health</c> 没有 <c>ocr_worker_state</c>），不预热。</summary>
    SkipUnsupported,

    /// <summary>OCR 已经热了或正在起来（<c>starting</c> / <c>ready</c> / <c>busy</c>，或进程内且已加载）。</summary>
    SkipAlreadyWarm,

    /// <summary>OCR 缺依赖或模型（<c>ocr_error.reason</c> 是配置类原因），预热必然失败。</summary>
    SkipOcrUnavailable,

    /// <summary>认不出的 <c>ocr_worker_state</c>（将来的引擎），保守起见不预热。</summary>
    SkipUnknownState,
}

/// <summary><see cref="IOcrPrewarmer.Prewarm"/> 的同步结果（后台任务是否启动）。</summary>
public enum OcrPrewarmStart
{
    /// <summary>已启动后台检查（之后可能发预热请求，也可能按 <c>/health</c> 跳过）。</summary>
    Started,

    /// <summary>上一次预热还在进行，跳过。</summary>
    SkippedInFlight,

    /// <summary>距上一次启动不到 <see cref="OcrPrewarmer.DefaultCooldown"/>，跳过。</summary>
    SkippedCooldown,

    /// <summary>已释放。</summary>
    Disposed,
}

/// <summary>按下框选快捷键时预热 OCR（#109）。实现必须立即返回、从不抛出。</summary>
public interface IOcrPrewarmer
{
    /// <summary>触发一次预热：同步部分只做节流判断，检查和请求在后台进行，不等结果。</summary>
    OcrPrewarmStart Prewarm();
}

/// <summary>
/// 框选前预热 OCR 子进程（#109，对接引擎 #104）：用户按下框选快捷键（或托盘「框选翻译」）时，在后台
/// <c>GET /health</c>，OCR 子进程已停止时 <c>POST /ocr</c> 一张 32×32 白图，把子进程拉起并加载模型；
/// 用户拖选区的 1–3 秒里消化掉冷启动（0.5–2 s）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>引擎没有专门的预热接口；白图 <c>/ocr</c> 与引擎自己的 <c>OcrEngine.warmup()</c>（160×48 白图）等价：
/// 已热时约 5 ms，识别结果为空，服务端只多一行不含内容的日志。</item>
/// <item>决策见 <see cref="Decide"/>：老引擎（没有 <c>ocr_worker_state</c>）、已热、缺依赖或模型时不发请求。</item>
/// <item>节流：同一时间最多一个预热在进行；距上一次启动不到 <see cref="Cooldown"/> 不再检查。</item>
/// <item>失败、超时、服务不可用一律吞掉；只有真的发了预热请求才写一行日志（耗时或失败类别）。</item>
/// <item>不改变正式请求：正式 <c>/ocr_translate</c> 照常发，预热没跑完时服务端让它排在预热后面（等的是同一次冷启动）。</item>
/// </list>
/// </remarks>
public sealed class OcrPrewarmer : IOcrPrewarmer, IDisposable
{
    /// <summary>默认节流间隔：距上一次启动不到 5 秒不再检查。</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 预热用的 32×32 纯白 PNG（RGB，95 字节）。识别结果为空；已热时服务端约 5 ms。
    /// </summary>
    public static ReadOnlyMemory<byte> BlankPng { get; } = new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x20, 0x08, 0x02, 0x00, 0x00, 0x00, 0xFC, 0x18, 0xED,
        0xA3, 0x00, 0x00, 0x00, 0x26, 0x49, 0x44, 0x41, 0x54, 0x78, 0xDA, 0xED, 0xCD, 0x31, 0x0D, 0x00,
        0x00, 0x0C, 0x03, 0xA0, 0xFA, 0x37, 0xDD, 0xAA, 0xD8, 0xB1, 0x04, 0x0C, 0x90, 0x1E, 0x8B, 0x40,
        0x20, 0x10, 0x08, 0x04, 0x02, 0x81, 0x40, 0x20, 0xF8, 0x12, 0x0C, 0x8B, 0x55, 0xF4, 0xA6, 0xDB,
        0x08, 0x71, 0x3D, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    };

    /// <summary>这些 <c>ocr_error.reason</c> 表示缺依赖、模型或清单：补齐之前预热必然失败，跳过。</summary>
    private static readonly HashSet<string> ConfigurationErrors = new(StringComparer.Ordinal)
    {
        "dependency_missing",
        "models_missing",
        "models_invalid",
        "manifest_unavailable",
    };

    private readonly EngineClient _client;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _gate = new();
    private bool _inFlight;
    private long? _lastStarted;
    private bool _disposed;

    /// <summary>创建。</summary>
    /// <param name="client">引擎客户端（不随本对象释放）。</param>
    /// <param name="logger">日志；只在真的发了预热请求时写一行。</param>
    /// <param name="timeProvider">节流计时与耗时测量；默认 <see cref="TimeProvider.System"/>。</param>
    /// <param name="cooldown">节流间隔；默认 <see cref="DefaultCooldown"/>。</param>
    public OcrPrewarmer(EngineClient client, IAppLogger? logger = null, TimeProvider? timeProvider = null, TimeSpan? cooldown = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _logger = logger ?? NullAppLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Cooldown = cooldown ?? DefaultCooldown;
    }

    /// <summary>节流间隔。</summary>
    public TimeSpan Cooldown { get; }

    /// <summary>最近一次启动的后台任务（测试用于等待；从不失败）。没启动过时为已完成的任务。</summary>
    public Task LastRun { get; private set; } = Task.CompletedTask;

    /// <summary>最近一次后台检查的结论；还没有结论（没启动过、进行中、<c>/health</c> 失败）时为 <see langword="null"/>。</summary>
    public OcrPrewarmDecision? LastDecision { get; private set; }

    /// <summary>实际发出过的预热请求数。</summary>
    public int RequestsSent => Volatile.Read(ref _requestsSent);

    private int _requestsSent;

    /// <summary>按 <c>/health</c> 决定是否预热（纯函数）。</summary>
    /// <param name="health">最近一次 <c>/health</c>。</param>
    public static OcrPrewarmDecision Decide(HealthResponse health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (health.OcrWorkerState is not { } state)
        {
            return OcrPrewarmDecision.SkipUnsupported;
        }

        if (health.OcrError?.Reason is { } reason && ConfigurationErrors.Contains(reason))
        {
            return OcrPrewarmDecision.SkipOcrUnavailable;
        }

        return state switch
        {
            OcrWorkerStates.Stopped => OcrPrewarmDecision.Prewarm,
            OcrWorkerStates.Starting or OcrWorkerStates.Ready or OcrWorkerStates.Busy => OcrPrewarmDecision.SkipAlreadyWarm,
            OcrWorkerStates.InProcess => health.OcrLoaded == true ? OcrPrewarmDecision.SkipAlreadyWarm : OcrPrewarmDecision.Prewarm,
            _ => OcrPrewarmDecision.SkipUnknownState,
        };
    }

    /// <inheritdoc />
    public OcrPrewarmStart Prewarm()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return OcrPrewarmStart.Disposed;
            }

            if (_inFlight)
            {
                return OcrPrewarmStart.SkippedInFlight;
            }

            if (_lastStarted is { } last && _timeProvider.GetElapsedTime(last) < Cooldown)
            {
                return OcrPrewarmStart.SkippedCooldown;
            }

            _inFlight = true;
            _lastStarted = _timeProvider.GetTimestamp();
            LastDecision = null;
            LastRun = Task.Run(() => RunAsync(_disposeCts.Token));
            return OcrPrewarmStart.Started;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var sent = false;
        long started = 0;
        try
        {
            var health = await _client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            var decision = Decide(health);
            LastDecision = decision;
            if (decision != OcrPrewarmDecision.Prewarm)
            {
                return;
            }

            started = _timeProvider.GetTimestamp();
            sent = true;
            Interlocked.Increment(ref _requestsSent);
            await _client.OcrAsync(BlankPng, EngineClient.AutoSource, cancellationToken).ConfigureAwait(false);
            _logger.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"框选预热：OCR 子进程为 {health.OcrWorkerState}，已预热（{_timeProvider.GetElapsedTime(started).TotalMilliseconds:F0} ms）"));
        }
        catch (Exception ex) when (ex is EngineException or OperationCanceledException or ObjectDisposedException or HttpRequestException)
        {
            // 预热只是尽力而为：服务未就绪、超时、OCR 不可用都不影响正式识别。没发请求时（/health 失败）不写日志。
            if (sent && !cancellationToken.IsCancellationRequested)
            {
                var kind = ex is EngineException engine ? engine.Kind.ToString() : ex.GetType().Name;
                _logger.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"框选预热失败（已忽略）：kind={kind} 用时 {_timeProvider.GetElapsedTime(started).TotalMilliseconds:F0} ms"));
            }
        }
#pragma warning disable CA1031 // 后台预热绝不能让未观察的异常冒出来。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (sent)
            {
                _logger.Info($"框选预热失败（已忽略）：{ex.GetType().Name}");
            }
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = false;
            }
        }
    }
}
