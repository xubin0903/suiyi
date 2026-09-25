using System.ComponentModel;
using System.Globalization;
using Suiyi.Core.Capture;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Engine;
using Suiyi.Core.Logging;
using Suiyi.Core.Popup;
using Suiyi.Core.Tray;

namespace Suiyi.Core.Flow;

/// <summary>
/// 主流程编排：复制翻译（#34，捕获文本 → 翻译 → 浮窗）与框选翻译（#58，框选 → OCR 翻译 → 浮窗，见
/// <c>TranslateFlowCoordinator.Region.cs</c>）。两条流程共用一套「当前请求」：互相取消、共用服务未就绪时的等待与自动补译、
/// 重试时重启服务等规则。只能在 UI 线程上调用；后台回调经 <c>dispatch</c> 切回 UI 线程。
/// <list type="bullet">
/// <item>服务就绪：浮窗 Loading → <see cref="ITranslationService.TranslateAsync"/> → Result / Error。</item>
/// <item>服务启动中 / 重启中：浮窗「正在准备翻译服务…」，在 <see cref="TranslateFlowOptions.ReadyWaitTimeout"/>（默认 30 s）内就绪则自动补译最后一次请求；
/// 超时显示「翻译服务启动超时」（可重试）；服务失败时立即提示可在托盘重启。浮窗不会一直停在「正在准备」。</item>
/// <item>最新优先：新请求（文本或框选）取消旧请求，旧请求的结果或错误一律丢弃。</item>
/// <item>重试按浮窗 <see cref="PopupViewModel.Mode"/> 分流：框选模式用原来那张 PNG 重新识别，复制模式重译上一次文本。</item>
/// <item>连接被拒（服务刚退出）：催监管器做健康检查；状态转为启动中 / 重启中则按「未就绪」等待并自动重译，
/// <see cref="TranslateFlowOptions.UnavailableConfirmTimeout"/> 内仍自称就绪则报「服务未运行」。</item>
/// <item>服务失败或启动超时后点「重试」：顺带重启服务（重启中不重复触发），再等待就绪并自动补译。</item>
/// <item>暂停监听时忽略 <see cref="ClipboardTrigger.Monitor"/>，快捷键与托盘仍可翻译。</item>
/// <item>自动监听遇到过长文本不弹窗，只在托盘提示一次（每次运行最多一次）。</item>
/// </list>
/// 日志只记录长度、尺寸、耗时与语种，不记录正文、识别文本与图片。
/// </summary>
public sealed partial class TranslateFlowCoordinator : IDisposable
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

    private TextRequest? _lastTextRequest;

    /// <summary>最近一次复制翻译请求；赋值时同步浮窗「重试」是否可用。</summary>
    private TextRequest? _lastText
    {
        get => _lastTextRequest;
        set
        {
            _lastTextRequest = value;
            UpdateRetryAvailability();
        }
    }
    private FlowRequest? _pending;
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _tooLongNotified;
    private bool _confirmingUnavailable;
    private bool _restartRequested;
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
    /// <param name="ocr">框选翻译服务（#56）；为 <see langword="null"/> 时不启用框选翻译。</param>
    /// <param name="region">框选入口（#55）；为 <see langword="null"/> 时不启用框选翻译。</param>
    public TranslateFlowCoordinator(
        ITranslationService translator,
        IEngineStatus engine,
        PopupViewModel popup,
        ITrayService tray,
        IAppLogger? logger = null,
        TimeProvider? timeProvider = null,
        Action<Action>? dispatch = null,
        Action<Action>? afterRender = null,
        TranslateFlowOptions? options = null,
        IOcrTranslationService? ocr = null,
        RegionCaptureTrigger? region = null)
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
        OcrLatency = new LatencyStats(Options.LatencyWindow);
        _readyWait = new OneShotTimer(_timeProvider, _dispatch);
        _ocr = ocr;
        _region = region;
        if (_region is not null)
        {
            _region.Failed += OnRegionCaptureFailed;
        }

        _engine.StateChanged += OnEngineStateChanged;
        _popup.RetryRequested += OnRetryRequested;
        _popup.SourceLanguageOverride += OnSourceLanguageOverride;
        _popup.Closed += OnPopupClosed;
        _popup.PropertyChanged += OnPopupPropertyChanged;
        UpdateRetryAvailability();
    }

    /// <summary>一次翻译完成并显示结果（端到端计时之后）。</summary>
    public event EventHandler<TranslateFlowCompletedEventArgs>? Completed;

    /// <summary>参数。</summary>
    public TranslateFlowOptions Options { get; }

    /// <summary>复制翻译最近若干次端到端延迟（热路径；等待服务就绪的请求不计入）。</summary>
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

        if (IsRegionCapturing)
        {
            _logger.Info("翻译：框选遮罩显示中，忽略文本捕获");
            return;
        }

        Start(new TextRequest(text, null, trigger, timestamp == 0 ? _timeProvider.GetTimestamp() : timestamp));
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

        if (IsRegionCapturing)
        {
            return;
        }

        // 用户主动触发（快捷键 / 托盘）时明确告诉他超了多少。
        CancelInFlight();
        _popup.ShowError(
            new PopupError(PopupErrorKind.TextTooLong) { Length = length, Limit = Options.ManualFilter.MaxChars },
            PopupContentMode.Text);
    }

    /// <summary>托盘「翻译剪贴板」：传入读取结果（由 App 读系统剪贴板）。</summary>
    public void TranslateClipboard(ClipboardReadResult read)
    {
        if (_disposed)
        {
            return;
        }

        if (IsRegionCapturing)
        {
            _logger.Info("翻译：框选遮罩显示中，忽略托盘「翻译剪贴板」");
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

        Start(new TextRequest(result.Text!, null, ClipboardTrigger.Tray, started));
    }

    /// <summary>重试上一次请求（浮窗「重试」）。</summary>
    /// <remarks>
    /// 按浮窗 <see cref="PopupViewModel.Mode"/> 分流：框选模式用原来那张 PNG 重新识别并翻译（不重新框选），复制模式重译上一次文本。
    /// 服务已失败、上一次是「服务未运行」，或上一次是「启动超时」且服务仍未就绪时，顺带重启服务（与托盘「重启翻译服务」同一条路径），
    /// 然后按正常流程等待就绪并自动补译。重启进行中不会重复触发。
    /// </remarks>
    public void Retry()
    {
        FlowRequest? last = _popup.Mode == PopupContentMode.Ocr ? _lastOcr : _lastText;
        if (last is null || _disposed)
        {
            _logger.Info($"翻译：没有可重试的请求（{_popup.Mode}）");
            return;
        }

        var state = _engine.State;
        var errorKind = _popup.Kind == PopupKind.Error ? _popup.Error?.Kind : null;
        var restart = state == EngineState.Failed
            || errorKind == PopupErrorKind.ServiceUnavailable
            || (errorKind == PopupErrorKind.EngineStartTimeout && state != EngineState.Ready);
        Start(last with { Started = _timeProvider.GetTimestamp(), WaitSince = null }, restart);
    }

    /// <summary>以指定原文语种重新翻译上一次文本（浮窗语种标签）。</summary>
    public void TranslateWithSource(string language)
    {
        if (_lastText is { } last && !_disposed && TrayLanguages.IsSupported(language))
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
        _popup.PropertyChanged -= OnPopupPropertyChanged;
        if (_region is not null)
        {
            _region.Failed -= OnRegionCaptureFailed;
        }

        CancelInFlight();
        _lastOcr = null;
        _readyWait.Dispose();
    }

    private void Start(FlowRequest request, bool restartEngine = false)
    {
        CancelInFlight();
        if (request is TextRequest text)
        {
            _lastText = text;
            _lastOcr = null; // 浮窗改显示复制翻译，旧的框选结果不会再显示，截图不再需要。
        }
        else if (request is OcrRequest ocr)
        {
            _lastOcr = ocr;
        }

        var state = _engine.State;
        if (restartEngine || _restartRequested)
        {
            // 重启进行中（服务可能仍短暂报告 Ready）：新请求一律等重启后的就绪事件。
            if (restartEngine)
            {
                RequestRestart();
            }

            WaitForEngine(request);
            return;
        }

        if (state == EngineState.Ready)
        {
            _ = RunAsync(request, waitedForEngine: false);
            return;
        }

        if (state == EngineState.Failed)
        {
            _logger.Info("翻译：服务已失败，提示在托盘重启");
            ShowError(request, PopupErrorMapper.EngineFailed(_engine.Failure));
            return;
        }

        // Starting / Restarting / Stopped：先提示，就绪后自动继续。
        _logger.Info($"翻译：服务未就绪（{state}），等待至多 {Options.ReadyWaitTimeout.TotalSeconds:0} 秒");
        WaitForEngine(request);
    }

    private void RequestRestart()
    {
        if (_restartRequested)
        {
            _logger.Info("翻译：重启已在进行，继续等待");
            return;
        }

        _restartRequested = true;
        _logger.Info("翻译：重试时顺带重启翻译服务");
        _ = RestartEngineAsync();
    }

    private async Task RestartEngineAsync()
    {
        try
        {
            await _engine.RestartAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 重启失败只记日志，状态由监管器事件反映。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.Error("翻译：重启翻译服务失败", ex);
            _dispatch(() => _restartRequested = false);
        }
    }

    /// <summary>
    /// 连接被拒但服务仍自称就绪：已催过健康检查，在 <see cref="TranslateFlowOptions.UnavailableConfirmTimeout"/> 内观察。
    /// 状态转为启动中 / 重启中则改为正常的就绪等待；就绪事件到达则补译；到时仍自称就绪则报「服务未运行」。
    /// </summary>
    private void ConfirmUnavailable(FlowRequest request)
    {
        _pending = request;
        _confirmingUnavailable = true;
        ShowPreparing(request);
        _readyWait.Start(Options.UnavailableConfirmTimeout, () =>
        {
            if (_pending is not { } pending || !_confirmingUnavailable)
            {
                return;
            }

            _confirmingUnavailable = false;
            switch (_engine.State)
            {
                case EngineState.Ready:
                    _pending = null;
                    _logger.Warn($"翻译：服务自称就绪但连接仍被拒（{Options.UnavailableConfirmTimeout.TotalSeconds:0} 秒）");
                    ShowError(pending, new PopupError(PopupErrorKind.ServiceUnavailable));
                    break;
                case EngineState.Failed:
                    _pending = null;
                    ShowError(pending, PopupErrorMapper.EngineFailed(_engine.Failure));
                    break;
                default:
                    WaitForEngine(pending);
                    break;
            }
        });
    }

    /// <summary>
    /// 进入「等服务就绪」：浮窗显示「正在准备」，就绪后补译 <paramref name="request"/>。
    /// 同一请求的多次等待（例如就绪后连接又被拒）共用一个时限，从第一次等待算起，保证在上限内进入结果或错误。
    /// </summary>
    private void WaitForEngine(FlowRequest request)
    {
        _confirmingUnavailable = false;
        var since = request.WaitSince ?? _timeProvider.GetTimestamp();
        _pending = request with { WaitSince = since };
        ShowPreparing(request);
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
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;
        if (_engine.State == EngineState.Ready)
        {
            // 连接被拒后服务仍报告就绪（没有发生重启）：按服务不可用报错，用户可重试。
            _logger.Warn("翻译：服务报告就绪但连接失败，等待超时");
            ShowError(pending, new PopupError(PopupErrorKind.ServiceUnavailable));
        }
        else
        {
            _logger.Warn($"翻译：等待服务就绪超时（{Options.ReadyWaitTimeout.TotalSeconds:0} 秒，服务状态 {_engine.State}）");
            ShowError(pending, new PopupError(PopupErrorKind.EngineStartTimeout));
        }
    }

    private void ShowPreparing(FlowRequest request)
    {
        if (request is OcrRequest ocr)
        {
            _popup.ShowPreparing(ocr.Anchor);
        }
        else
        {
            _popup.ShowPreparing();
        }
    }

    private void ShowError(FlowRequest request, PopupError error)
    {
        if (request is OcrRequest ocr)
        {
            _popup.ShowError(error, PopupContentMode.Ocr, ocr.Anchor);
        }
        else
        {
            _popup.ShowError(error, PopupContentMode.Text);
        }
    }

    private Task RunAsync(FlowRequest request, bool waitedForEngine) => request switch
    {
        OcrRequest ocr => RunOcrAsync(ocr, waitedForEngine),
        TextRequest text => RunTextAsync(text, waitedForEngine),
        _ => Task.CompletedTask,
    };

    private async Task RunTextAsync(TextRequest request, bool waitedForEngine)
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

    private void OnSucceeded(int generation, TextRequest request, TranslationOutcome outcome, bool waitedForEngine)
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
        LogFailure(request, ex);
        if (ex.Kind == EngineErrorKind.Unavailable)
        {
            // 服务多半刚退出、监管器还没察觉：催一次健康检查，浮窗显示「正在准备」，恢复后自动重译。
            _engine.RequestHealthCheck();
            switch (_engine.State)
            {
                case EngineState.Failed:
                    ShowError(request, PopupErrorMapper.EngineFailed(_engine.Failure));
                    break;
                case EngineState.Ready:
                    ConfirmUnavailable(request);
                    break;
                default:
                    WaitForEngine(request);
                    break;
            }

            return;
        }

        ShowError(request, PopupErrorMapper.Map(ex));
    }

    private void LogFailure(FlowRequest request, EngineException ex)
    {
        switch (request)
        {
            case TextRequest text:
                _logger.Warn($"翻译失败：trigger={text.Trigger} chars={text.Text.Length} kind={ex.Kind}：{ex.Message}");
                break;
            case OcrRequest ocr:
                // 服务端的错误说明不写日志（框选翻译只记尺寸、字节数与错误码）。
                _logger.Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"框选翻译失败：trigger={ocr.Trigger} size={ocr.Width}x{ocr.Height} bytes={ocr.Png.Length} kind={ex.Kind} code={ex.ErrorCode ?? "-"} status={ex.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}"));
                break;
            default:
                break;
        }
    }

    private void OnEngineStateChanged(object? sender, EngineStateChangedEventArgs e) => _dispatch(() =>
    {
        if (e.State is EngineState.Ready or EngineState.Failed or EngineState.Stopped)
        {
            _restartRequested = false;
        }

        if (_disposed || _pending is not { } pending)
        {
            return;
        }

        if (_confirmingUnavailable && e.State is EngineState.Starting or EngineState.Restarting)
        {
            _logger.Info($"翻译：服务正在重启（{e.State}），等待就绪");
            WaitForEngine(pending);
            return;
        }

        if (e.State == EngineState.Ready)
        {
            _pending = null;
            _confirmingUnavailable = false;
            _readyWait.Stop();
            _logger.Info("翻译：服务已就绪，继续翻译等待中的文本");
            _ = RunAsync(pending, waitedForEngine: true);
        }
        else if (e.State == EngineState.Failed)
        {
            _pending = null;
            _confirmingUnavailable = false;
            _readyWait.Stop();
            ShowError(pending, PopupErrorMapper.EngineFailed(e.Failure ?? _engine.Failure));
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

        // 截图不随浮窗关闭丢弃：托盘左键重新显示时仍可重试。
    }

    private void OnPopupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 浮窗改为复制翻译的内容（包括「文本过长」等不经过请求的提示）：旧的框选内容再也不会显示，丢掉截图。
        if (e.PropertyName == nameof(PopupViewModel.Mode) && _popup.Mode == PopupContentMode.Text && _lastOcrRequest is not null)
        {
            _lastOcr = null;
        }
    }

    private void UpdateRetryAvailability() =>
        _popup.SetRetryAvailability(text: _lastTextRequest is not null, ocr: _lastOcrRequest is not null);

    private void CancelInFlight()
    {
        _generation++;
        _pending = null;
        _confirmingUnavailable = false;
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

    /// <summary>一次请求（文本或框选）。</summary>
    /// <param name="Started">端到端计时起点（<see cref="TimeProvider.GetTimestamp"/>）。</param>
    /// <param name="WaitSince">开始等服务就绪的时刻；<see langword="null"/> 表示还没等过。重试、改语种时重新计时。</param>
    private abstract record FlowRequest(long Started, long? WaitSince);

    /// <summary>复制翻译请求。</summary>
    private sealed record TextRequest(string Text, string? SourceOverride, ClipboardTrigger Trigger, long Started, long? WaitSince = null)
        : FlowRequest(Started, WaitSince);
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
