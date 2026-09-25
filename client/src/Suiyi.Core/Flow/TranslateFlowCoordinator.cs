using System.Globalization;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Engine;
using Suiyi.Core.Logging;
using Suiyi.Core.Popup;
using Suiyi.Core.Tray;

namespace Suiyi.Core.Flow;

/// <summary>
/// 主流程编排（#34）：捕获文本 → 翻译 → 浮窗。只能在 UI 线程上调用；后台回调经 <c>dispatch</c> 切回 UI 线程。
/// <list type="bullet">
/// <item>服务就绪：浮窗 Loading → <see cref="ITranslationService.TranslateAsync"/> → Result / Error。</item>
/// <item>服务启动中 / 重启中：浮窗「正在准备翻译服务…」，在 <see cref="TranslateFlowOptions.ReadyWaitTimeout"/>（默认 30 s）内就绪则自动补译最后一次请求；
/// 超时显示「翻译服务启动超时」（可重试）；服务失败时立即提示可在托盘重启。浮窗不会一直停在「正在准备」。</item>
/// <item>最新优先：新请求取消旧请求，旧请求的结果或错误一律丢弃。</item>
/// <item>连接被拒（服务刚退出）：催监管器做健康检查，按「未就绪」处理，恢复后自动重译。</item>
/// <item>暂停监听时忽略 <see cref="ClipboardTrigger.Monitor"/>，快捷键与托盘仍可翻译。</item>
/// <item>自动监听遇到过长文本不弹窗，只在托盘提示一次（每次运行最多一次）。</item>
/// </list>
/// 日志只记录长度、耗时与语种，不记录正文。
/// </summary>
public sealed class TranslateFlowCoordinator : IDisposable
{
    private const string AppTitle = "随译";

    private readonly ITranslationService _translator;
    private readonly IEngineStatus _engine;
    private readonly PopupViewModel _popup;
    private readonly ITrayService _tray;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Action<Action> _dispatch;
    private readonly Action<Action> _afterRender;
    private readonly OneShotTimer _readyWait;

    private FlowRequest? _last;
    private FlowRequest? _pending;
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _tooLongNotified;
    private bool _disposed;

    /// <summary>创建编排器。</summary>
    /// <param name="translator">翻译服务（生产环境为 <see cref="TranslationService"/>，目标语言每次从设置读取）。</param>
    /// <param name="engine">服务状态（生产环境为 <see cref="EngineSupervisor"/>）。</param>
    /// <param name="popup">浮窗。</param>
    /// <param name="tray">托盘（读取暂停状态、显示提示）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="timeProvider">时钟，测试时注入。</param>
    /// <param name="dispatch">切回 UI 线程；默认直接调用（测试用）。</param>
    /// <param name="afterRender">在浮窗渲染完成后执行（用于端到端计时）；默认直接调用。</param>
    /// <param name="options">参数。</param>
    public TranslateFlowCoordinator(
        ITranslationService translator,
        IEngineStatus engine,
        PopupViewModel popup,
        ITrayService tray,
        IAppLogger? logger = null,
        TimeProvider? timeProvider = null,
        Action<Action>? dispatch = null,
        Action<Action>? afterRender = null,
        TranslateFlowOptions? options = null)
    {
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _popup = popup ?? throw new ArgumentNullException(nameof(popup));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _logger = logger ?? NullAppLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dispatch = dispatch ?? (a => a());
        _afterRender = afterRender ?? (a => a());
        Options = options ?? new TranslateFlowOptions();
        Latency = new LatencyStats(Options.LatencyWindow);
        _readyWait = new OneShotTimer(_timeProvider, _dispatch);

        _engine.StateChanged += OnEngineStateChanged;
        _popup.RetryRequested += OnRetryRequested;
        _popup.SourceLanguageOverride += OnSourceLanguageOverride;
        _popup.Closed += OnPopupClosed;
    }

    /// <summary>一次翻译完成并显示结果（端到端计时之后）。</summary>
    public event EventHandler<TranslateFlowCompletedEventArgs>? Completed;

    /// <summary>参数。</summary>
    public TranslateFlowOptions Options { get; }

    /// <summary>最近若干次端到端延迟（热路径；等待服务就绪的请求不计入）。</summary>
    public LatencyStats Latency { get; }

    /// <summary>是否有请求在等服务就绪。</summary>
    public bool IsWaitingForEngine => _pending is not null;

    /// <summary>剪贴板监听或快捷键捕获到文本。</summary>
    /// <param name="text">文本。</param>
    /// <param name="trigger">来源。</param>
    /// <param name="timestamp">触发时刻（<see cref="TimeProvider.GetTimestamp"/>），0 表示用当前时刻。</param>
    public void OnTextCaptured(string text, ClipboardTrigger trigger, long timestamp = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed)
        {
            return;
        }

        if (trigger == ClipboardTrigger.Monitor && _tray.State.Paused)
        {
            _logger.Info("翻译：已暂停监听，忽略自动捕获");
            return;
        }

        Start(new FlowRequest(text, null, trigger, timestamp == 0 ? _timeProvider.GetTimestamp() : timestamp));
    }

    /// <summary>剪贴板监听或快捷键的文本未被接受。</summary>
    public void OnTextRejected(RejectReason reason, int length, ClipboardTrigger trigger)
    {
        if (_disposed || reason != RejectReason.TooLong)
        {
            return;
        }

        if (trigger == ClipboardTrigger.Monitor)
        {
            if (_tray.State.Paused || _tooLongNotified)
            {
                return;
            }

            _tooLongNotified = true;
            _tray.ShowNotification(AppTitle, string.Create(CultureInfo.InvariantCulture, $"文本过长（{length} 字），可用快捷键手动翻译"));
            return;
        }

        // 用户主动触发（快捷键 / 托盘）时明确告诉他超了多少。
        CancelInFlight();
        _popup.ShowError(new PopupError(PopupErrorKind.TextTooLong) { Length = length, Limit = Options.ManualFilter.MaxChars });
    }

    /// <summary>托盘「翻译剪贴板」：传入读取结果（由 App 读系统剪贴板）。</summary>
    public void TranslateClipboard(ClipboardReadResult read)
    {
        if (_disposed)
        {
            return;
        }

        var started = _timeProvider.GetTimestamp();
        switch (read.Status)
        {
            case ClipboardReadStatus.Busy:
                _tray.ShowNotification(AppTitle, "剪贴板被其他程序占用，请稍后再试");
                return;
            case ClipboardReadStatus.PrivateContent:
                _tray.ShowNotification(AppTitle, "剪贴板内容被标记为隐私，未读取");
                return;
            case ClipboardReadStatus.NoText:
                _tray.ShowNotification(AppTitle, "剪贴板里没有文本");
                return;
            case ClipboardReadStatus.Text:
            default:
                break;
        }

        var result = ClipboardTextFilter.Classify(read.Text, Options.ManualFilter);
        if (!result.IsAccepted)
        {
            if (result.Reason == RejectReason.TooLong)
            {
                OnTextRejected(RejectReason.TooLong, result.Length, ClipboardTrigger.Tray);
            }
            else
            {
                _tray.ShowNotification(AppTitle, "剪贴板里没有可翻译的文字");
            }

            return;
        }

        Start(new FlowRequest(result.Text!, null, ClipboardTrigger.Tray, started));
    }

    /// <summary>重试上一次请求（浮窗「重试」）。</summary>
    public void Retry()
    {
        if (_last is { } last && !_disposed)
        {
            Start(last with { Started = _timeProvider.GetTimestamp(), WaitSince = null });
        }
    }

    /// <summary>以指定原文语种重新翻译上一次文本（浮窗语种标签）。</summary>
    public void TranslateWithSource(string language)
    {
        if (_last is { } last && !_disposed && TrayLanguages.IsSupported(language))
        {
            Start(last with { SourceOverride = language.ToLowerInvariant(), Started = _timeProvider.GetTimestamp(), WaitSince = null });
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.StateChanged -= OnEngineStateChanged;
        _popup.RetryRequested -= OnRetryRequested;
        _popup.SourceLanguageOverride -= OnSourceLanguageOverride;
        _popup.Closed -= OnPopupClosed;
        CancelInFlight();
        _readyWait.Dispose();
    }

    private void Start(FlowRequest request)
    {
        CancelInFlight();
        _last = request;
        var state = _engine.State;
        if (state == EngineState.Ready)
        {
            _ = RunAsync(request, waitedForEngine: false);
            return;
        }

        if (state == EngineState.Failed)
        {
            _logger.Info("翻译：服务已失败，提示在托盘重启");
            _popup.ShowError(PopupErrorMapper.EngineFailed());
            return;
        }

        // Starting / Restarting / Stopped：先提示，就绪后自动继续。
        _logger.Info($"翻译：服务未就绪（{state}），等待至多 {Options.ReadyWaitTimeout.TotalSeconds:0} 秒");
        WaitForEngine(request);
    }

    /// <summary>
    /// 进入「等服务就绪」：浮窗显示「正在准备」，就绪后补译 <paramref name="request"/>。
    /// 同一请求的多次等待（例如就绪后连接又被拒）共用一个时限，从第一次等待算起，保证在上限内进入结果或错误。
    /// </summary>
    private void WaitForEngine(FlowRequest request)
    {
        var since = request.WaitSince ?? _timeProvider.GetTimestamp();
        _pending = request with { WaitSince = since };
        _popup.ShowPreparing();
        var remaining = Options.ReadyWaitTimeout - _timeProvider.GetElapsedTime(since);
        if (remaining <= TimeSpan.Zero)
        {
            OnReadyWaitTimeout();
            return;
        }

        _readyWait.Start(remaining, OnReadyWaitTimeout);
    }

    private void OnReadyWaitTimeout()
    {
        if (_pending is null)
        {
            return;
        }

        _pending = null;
        if (_engine.State == EngineState.Ready)
        {
            // 连接被拒后服务仍报告就绪（没有发生重启）：按服务不可用报错，用户可重试。
            _logger.Warn("翻译：服务报告就绪但连接失败，等待超时");
            _popup.ShowError(new PopupError(PopupErrorKind.ServiceUnavailable));
        }
        else
        {
            _logger.Warn($"翻译：等待服务就绪超时（{Options.ReadyWaitTimeout.TotalSeconds:0} 秒，服务状态 {_engine.State}）");
            _popup.ShowError(new PopupError(PopupErrorKind.EngineStartTimeout));
        }
    }

    private async Task RunAsync(FlowRequest request, bool waitedForEngine)
    {
        var generation = ++_generation;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _popup.ShowLoading(request.Text);

        TranslationOutcome outcome;
        try
        {
            outcome = await _translator.TranslateAsync(request.Text, request.SourceOverride, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (EngineException ex)
        {
            _dispatch(() => OnFailed(generation, request, ex));
            return;
        }
#pragma warning disable CA1031 // 翻译失败不能让异常逃逸到消息循环。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _dispatch(() => OnFailed(generation, request, new EngineException(EngineErrorKind.Unknown, ex.Message, ex)));
            return;
        }
        finally
        {
            cts.Dispose();
        }

        _dispatch(() => OnSucceeded(generation, request, outcome, waitedForEngine));
    }

    private void OnSucceeded(int generation, FlowRequest request, TranslationOutcome outcome, bool waitedForEngine)
    {
        if (generation != _generation || _disposed)
        {
            return; // 已被更新的请求取代。
        }

        _cts = null;
        _popup.ShowResult(new PopupResult(outcome.Text, outcome.SourceLanguage, outcome.TargetLanguage)
        {
            SourceDetected = outcome.SourceDetected,
            Elapsed = outcome.ClientElapsed,
        });

        _afterRender(() =>
        {
            var e2e = _timeProvider.GetElapsedTime(request.Started);
            if (!waitedForEngine)
            {
                Latency.Add(e2e.TotalMilliseconds);
            }

            _logger.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"翻译完成：trigger={request.Trigger} chars={request.Text.Length} {outcome.SourceLanguage}→{outcome.TargetLanguage}"
                + $"{(outcome.Retargeted ? "（改译）" : string.Empty)} route={string.Join('+', outcome.Route)} requests={outcome.RequestCount}"
                + $" e2e_ms={e2e.TotalMilliseconds:0} http_ms={outcome.ClientElapsed.TotalMilliseconds:0} server_ms={outcome.ServerElapsedMs:0}"
                + $"{(waitedForEngine ? "（含等待服务就绪，不计入统计）" : string.Empty)}；{Latency.Summary()}"));
            Completed?.Invoke(this, new TranslateFlowCompletedEventArgs(request.Trigger, outcome, e2e, waitedForEngine));
        });
    }

    private void OnFailed(int generation, FlowRequest request, EngineException ex)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        _cts = null;
        _logger.Warn($"翻译失败：trigger={request.Trigger} chars={request.Text.Length} kind={ex.Kind}：{ex.Message}");
        if (ex.Kind == EngineErrorKind.Unavailable)
        {
            // 服务多半刚退出、监管器还没察觉：催一次健康检查，浮窗显示「正在准备」，恢复后自动重译。
            _engine.RequestHealthCheck();
            if (_engine.State == EngineState.Failed)
            {
                _popup.ShowError(PopupErrorMapper.EngineFailed());
            }
            else
            {
                WaitForEngine(request);
            }

            return;
        }

        _popup.ShowError(PopupErrorMapper.Map(ex));
    }

    private void OnEngineStateChanged(object? sender, EngineStateChangedEventArgs e) => _dispatch(() =>
    {
        if (_disposed || _pending is not { } pending)
        {
            return;
        }

        if (e.State == EngineState.Ready)
        {
            _pending = null;
            _readyWait.Stop();
            _logger.Info("翻译：服务已就绪，继续翻译等待中的文本");
            _ = RunAsync(pending, waitedForEngine: true);
        }
        else if (e.State == EngineState.Failed)
        {
            _pending = null;
            _readyWait.Stop();
            _popup.ShowError(PopupErrorMapper.EngineFailed());
        }
    });

    private void OnRetryRequested(object? sender, EventArgs e) => Retry();

    private void OnSourceLanguageOverride(object? sender, PopupSourceOverrideEventArgs e) => TranslateWithSource(e.Language);

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        // 用户关掉浮窗：不再需要这次翻译。
        if (e.Reason == PopupCloseReason.User)
        {
            CancelInFlight();
        }
    }

    private void CancelInFlight()
    {
        _generation++;
        _pending = null;
        _readyWait.Stop();
        if (_cts is { } cts)
        {
            _cts = null;
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已完成。
            }
        }
    }

    /// <param name="Text"></param>

    /// <param name="SourceOverride"></param>
    /// <param name="Trigger"></param>
    /// <param name="Started"></param>    /// <param name="WaitSince">开始等服务就绪的时刻；<see langword="null"/> 表示还没等过。重试、改语种时重新计时。</param>
    private sealed record FlowRequest(string Text, string? SourceOverride, ClipboardTrigger Trigger, long Started, long? WaitSince = null);
}

/// <summary><see cref="TranslateFlowCoordinator.Completed"/> 参数。</summary>
/// <param name="trigger">来源。</param>
/// <param name="outcome">翻译结果。</param>
/// <param name="endToEnd">从触发到浮窗渲染完成。</param>
/// <param name="waitedForEngine">是否等待过服务就绪（不计入延迟统计）。</param>
public sealed class TranslateFlowCompletedEventArgs(ClipboardTrigger trigger, TranslationOutcome outcome, TimeSpan endToEnd, bool waitedForEngine) : EventArgs
{
    /// <summary>来源。</summary>
    public ClipboardTrigger Trigger { get; } = trigger;

    /// <summary>翻译结果。</summary>
    public TranslationOutcome Outcome { get; } = outcome;

    /// <summary>端到端耗时。</summary>
    public TimeSpan EndToEnd { get; } = endToEnd;

    /// <summary>是否等待过服务就绪。</summary>
    public bool WaitedForEngine { get; } = waitedForEngine;
}
