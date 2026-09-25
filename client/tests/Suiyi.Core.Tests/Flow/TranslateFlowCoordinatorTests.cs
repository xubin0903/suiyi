using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Engine;
using Suiyi.Core.Flow;
using Suiyi.Core.Popup;
using Suiyi.Core.Tests.Clipboard;
using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Flow;

public sealed class TranslateFlowCoordinatorTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeTranslationService _translator = new();
    private readonly FakeEngineStatus _engine = new();
    private readonly TrayController _tray = new();
    private readonly RecordingLogger _logger = new();
    private readonly List<string> _notifications = [];
    private readonly List<TranslateFlowCompletedEventArgs> _completed = [];
    private readonly PopupViewModel _popup;
    private readonly TranslateFlowCoordinator _flow;

    public TranslateFlowCoordinatorTests()
    {
        _popup = new PopupViewModel(timeProvider: _time);
        _tray.NotificationRequested += (_, e) => _notifications.Add(e.Message);
        _flow = new TranslateFlowCoordinator(_translator, _engine, _popup, _tray, _logger, _time);
        _flow.Completed += (_, e) => _completed.Add(e);
    }

    public void Dispose()
    {
        _flow.Dispose();
        _popup.Dispose();
    }

    // ---- 成功 ----

    [Fact]
    public void Ready_Success_ShowsLoadingThenResult_AndRecordsLatency()
    {
        var started = _time.GetTimestamp();
        _time.Advance(TimeSpan.FromMilliseconds(50)); // 防抖等

        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor, started);

        Assert.Equal(PopupKind.Loading, _popup.Kind);
        var call = Assert.Single(_translator.Calls);
        Assert.Equal("Hello", call.Text);
        Assert.Null(call.SourceOverride);

        _time.Advance(TimeSpan.FromMilliseconds(250));
        _translator.Complete(0, "你好", "en", "zh");

        Assert.Equal(PopupKind.Result, _popup.Kind);
        Assert.Equal("你好", _popup.Translation);
        var done = Assert.Single(_completed);
        Assert.Equal(ClipboardTrigger.Monitor, done.Trigger);
        Assert.Equal(TimeSpan.FromMilliseconds(300), done.EndToEnd);
        Assert.False(done.WaitedForEngine);
        Assert.Equal(1, _flow.Latency.Count);
        Assert.Equal(300, _flow.Latency.Percentile(95));
    }

    [Fact]
    public void Success_LogsTimingsWithoutText()
    {
        _flow.OnTextCaptured("Secret sentence", ClipboardTrigger.Hotkey);
        _translator.Complete(0, "机密句子");

        var line = Assert.Single(_logger.Messages, m => m.Contains("翻译完成", StringComparison.Ordinal));
        Assert.Contains("trigger=Hotkey", line, StringComparison.Ordinal);
        Assert.Contains("chars=15", line, StringComparison.Ordinal);
        Assert.Contains("e2e_ms=0", line, StringComparison.Ordinal);
        Assert.Contains("http_ms=60", line, StringComparison.Ordinal);
        Assert.Contains("server_ms=42", line, StringComparison.Ordinal);
        Assert.Contains("P95", line, StringComparison.Ordinal);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("Secret", StringComparison.Ordinal) || m.Contains("机密", StringComparison.Ordinal));
    }

    [Fact]
    public void Success_EndToEndMeasuredInsideAfterRender()
    {
        Action? deferred = null;
        using var popup = new PopupViewModel(timeProvider: _time);
        using var flow = new TranslateFlowCoordinator(_translator, _engine, popup, _tray, timeProvider: _time, afterRender: a => deferred = a);
        TimeSpan? e2e = null;
        flow.Completed += (_, e) => e2e = e.EndToEnd;

        flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _translator.Complete(0);
        Assert.Equal(PopupKind.Result, popup.Kind);
        Assert.Null(e2e);

        _time.Advance(TimeSpan.FromMilliseconds(16)); // 渲染一帧
        deferred!();

        Assert.Equal(TimeSpan.FromMilliseconds(16), e2e);
    }

    // ---- 错误映射 ----

    [Theory]
    [InlineData(EngineErrorKind.Timeout, PopupErrorKind.Timeout)]
    [InlineData(EngineErrorKind.UnsupportedPair, PopupErrorKind.MissingModels)]
    [InlineData(EngineErrorKind.TextTooLong, PopupErrorKind.TextTooLong)]
    [InlineData(EngineErrorKind.DetectFailed, PopupErrorKind.DetectFailed)]
    [InlineData(EngineErrorKind.InvalidRequest, PopupErrorKind.Other)]
    [InlineData(EngineErrorKind.Internal, PopupErrorKind.Other)]
    [InlineData(EngineErrorKind.Unknown, PopupErrorKind.Other)]
    public void EngineError_MapsToPopupError(EngineErrorKind kind, PopupErrorKind expected)
    {
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);
        _translator.Fail(0, new EngineException(kind, "boom"));

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(expected, _popup.Error!.Kind);
        Assert.Empty(_completed);
        Assert.Equal(0, _flow.Latency.Count);
    }

    [Fact]
    public void UnsupportedPair_ShowsMissingModels()
    {
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);
        _translator.Fail(0, new EngineException(EngineErrorKind.UnsupportedPair, "x") { MissingModels = ["opus-mt-en-ja"] });

        Assert.Equal("未安装语向模型：opus-mt-en-ja", _popup.ErrorMessage);
    }

    [Fact]
    public void UnexpectedException_MapsToOther()
    {
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);
        _translator.Fail(0, new InvalidOperationException("bug"));

        Assert.Equal(PopupErrorKind.Other, _popup.Error!.Kind);
    }

    // ---- 服务未就绪 ----

    [Fact]
    public void NotReady_ShowsPreparing_ThenContinuesWhenReady()
    {
        _engine.State = EngineState.Starting;

        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);

        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.True(_popup.IsVisible);
        Assert.Empty(_translator.Calls);
        Assert.True(_flow.IsWaitingForEngine);

        _time.Advance(TimeSpan.FromSeconds(3));
        _engine.Raise(EngineState.Ready);

        Assert.False(_flow.IsWaitingForEngine);
        Assert.Equal("Hello", Assert.Single(_translator.Calls).Text);
        _translator.Complete(0);
        Assert.Equal(PopupKind.Result, _popup.Kind);
        Assert.True(Assert.Single(_completed).WaitedForEngine);
        Assert.Equal(0, _flow.Latency.Count); // 含等待，不计入热路径统计
    }

    [Theory]
    [InlineData(EngineState.Stopped)]
    [InlineData(EngineState.Restarting)]
    public void OtherNotReadyStates_AlsoWait(EngineState state)
    {
        _engine.State = state;

        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);

        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.True(_flow.IsWaitingForEngine);
    }

    [Fact]
    public void NotReady_ReadyJustBeforeLimit_TranslatesLastRequest_PopupStaysVisible()
    {
        _engine.State = EngineState.Starting;
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);

        _time.Advance(TimeSpan.FromSeconds(29.9)); // 超过浮窗 8 s 自动消失，「正在准备」仍在
        Assert.True(_popup.IsVisible);
        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.True(_flow.IsWaitingForEngine);

        _engine.Raise(EngineState.Ready);
        _translator.Complete(0);

        Assert.Equal("Hello", Assert.Single(_translator.Calls).Text);
        Assert.Equal(PopupKind.Result, _popup.Kind);
    }

    [Fact]
    public void NotReady_TimesOutAfter30Seconds_ShowsStartTimeoutError()
    {
        _engine.State = EngineState.Starting;
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);

        _time.Advance(TimeSpan.FromSeconds(29.999));
        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(_flow.IsWaitingForEngine);
        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupErrorKind.EngineStartTimeout, _popup.Error!.Kind);
        Assert.Equal("翻译服务启动超时，可点「重试」，或在托盘菜单「重启翻译服务」", _popup.ErrorMessage);
        Assert.True(_popup.CanRetry);

        _engine.Raise(EngineState.Ready); // 超时后才就绪：不再自动翻译，等用户重试
        Assert.Empty(_translator.Calls);
    }

    [Fact]
    public void StartTimeout_Retry_WaitsAgain_ThenTranslates()
    {
        _engine.State = EngineState.Starting;
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _time.Advance(TimeSpan.FromSeconds(30));

        _popup.RequestRetry();

        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.True(_flow.IsWaitingForEngine);
        _time.Advance(TimeSpan.FromSeconds(29)); // 重试重新计满 30 s
        Assert.True(_flow.IsWaitingForEngine);

        _engine.Raise(EngineState.Ready);
        _translator.Complete(0);

        Assert.Equal("Hello", Assert.Single(_translator.Calls).Text);
        Assert.Equal(PopupKind.Result, _popup.Kind);
    }

    [Fact]
    public void StartTimeout_RetryTimesOutAgain()
    {
        _engine.State = EngineState.Starting;
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _time.Advance(TimeSpan.FromSeconds(30));
        _popup.RequestRetry();

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(PopupErrorKind.EngineStartTimeout, _popup.Error!.Kind);
        Assert.Empty(_translator.Calls);
    }

    [Fact]
    public void ReadyWaitTimeout_IsConfigurable()
    {
        using var popup = new PopupViewModel(timeProvider: _time);
        using var flow = new TranslateFlowCoordinator(
            _translator, _engine, popup, _tray, timeProvider: _time, options: new TranslateFlowOptions { ReadyWaitTimeout = TimeSpan.FromSeconds(10) });
        _engine.State = EngineState.Restarting;

        flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(PopupErrorKind.EngineStartTimeout, popup.Error!.Kind);
    }

    [Fact]
    public void Default_ReadyWaitTimeoutIs30Seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(30), new TranslateFlowOptions().ReadyWaitTimeout);

    [Fact]
    public void RepeatedUnavailable_SharesOneDeadline_NeverStuckInPreparing()
    {
        // 服务反复「就绪 → 连接被拒」：同一请求的多次等待共用 30 s 时限，不会一直停在「正在准备」。
        _engine.OnHealthCheck = () => _engine.Raise(EngineState.Restarting);
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _translator.Fail(0, new EngineException(EngineErrorKind.Unavailable, "refused"));

        _time.Advance(TimeSpan.FromSeconds(20));
        _engine.Raise(EngineState.Ready);
        _translator.Fail(1, new EngineException(EngineErrorKind.Unavailable, "refused"));
        Assert.Equal(PopupKind.Preparing, _popup.Kind);

        _time.Advance(TimeSpan.FromSeconds(10)); // 从第一次等待算起满 30 s

        Assert.Equal(PopupErrorKind.EngineStartTimeout, _popup.Error!.Kind);
        Assert.False(_flow.IsWaitingForEngine);
    }

    [Fact]
    public void UnavailableAfterDeadlinePassed_ErrorsImmediately()
    {
        _engine.State = EngineState.Starting;
        _engine.OnHealthCheck = () => _engine.State = EngineState.Restarting;
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _time.Advance(TimeSpan.FromSeconds(29));
        _engine.Raise(EngineState.Ready);
        _time.Advance(TimeSpan.FromSeconds(2)); // 请求在路上时越过了时限

        _translator.Fail(0, new EngineException(EngineErrorKind.Unavailable, "refused"));

        Assert.Equal(PopupErrorKind.EngineStartTimeout, _popup.Error!.Kind);
        Assert.False(_flow.IsWaitingForEngine);
    }

    [Fact]
    public void NewRequestWhileWaiting_RestartsDeadlineForLatestRequest()
    {
        _engine.State = EngineState.Starting;
        _flow.OnTextCaptured("first", ClipboardTrigger.Monitor);
        _time.Advance(TimeSpan.FromSeconds(20));
        _flow.OnTextCaptured("second", ClipboardTrigger.Monitor);

        _time.Advance(TimeSpan.FromSeconds(20)); // 距第一次 40 s，距第二次 20 s
        Assert.True(_flow.IsWaitingForEngine);
        _engine.Raise(EngineState.Ready);

        Assert.Equal("second", Assert.Single(_translator.Calls).Text);
    }

    [Fact]
    public void WaitingThenFailed_ShowsRestartHintImmediately_NoLaterTimeout()
    {
        _engine.State = EngineState.Starting;
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);
        _time.Advance(TimeSpan.FromSeconds(12));

        _engine.Raise(EngineState.Failed);
        Assert.Equal(PopupErrorMapper.EngineFailedMessage, _popup.ErrorMessage);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(PopupErrorMapper.EngineFailedMessage, _popup.ErrorMessage);
    }

    [Fact]
    public void Failed_ShowsRestartHint()
    {
        _engine.State = EngineState.Failed;

        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupErrorKind.ServiceUnavailable, _popup.Error!.Kind);
        Assert.Equal(PopupErrorMapper.EngineFailedMessage, _popup.ErrorMessage);
        Assert.Empty(_translator.Calls);
    }

    [Fact]
    public void Waiting_EngineFails_ShowsRestartHint()
    {
        _engine.State = EngineState.Starting;
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);

        _engine.Raise(EngineState.Failed);

        Assert.Equal(PopupErrorMapper.EngineFailedMessage, _popup.ErrorMessage);
        Assert.False(_flow.IsWaitingForEngine);
        Assert.Empty(_translator.Calls);
    }

    [Fact]
    public void Waiting_StateChangesWithoutPending_AreIgnored()
    {
        _engine.Raise(EngineState.Restarting);
        _engine.Raise(EngineState.Ready);

        Assert.Empty(_translator.Calls);
        Assert.Equal(PopupKind.None, _popup.Kind);
    }

    [Fact]
    public void Unavailable_RequestsHealthCheck_WaitsAndRetranslatesAfterRestart()
    {
        _engine.OnHealthCheck = () => _engine.Raise(EngineState.Restarting);
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);

        _translator.Fail(0, new EngineException(EngineErrorKind.Unavailable, "refused"));

        Assert.Equal(1, _engine.HealthChecks);
        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.True(_flow.IsWaitingForEngine);

        _engine.Raise(EngineState.Ready);

        Assert.Equal(2, _translator.Calls.Count);
        Assert.Equal("Hello", _translator.Calls[1].Text);
        _translator.Complete(1);
        Assert.Equal(PopupKind.Result, _popup.Kind);
    }

    [Fact]
    public void Unavailable_EngineStillReportsReady_ErrorsAfterTimeout()
    {
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _translator.Fail(0, new EngineException(EngineErrorKind.Unavailable, "refused"));
        Assert.Equal(PopupKind.Preparing, _popup.Kind);

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupErrorKind.ServiceUnavailable, _popup.Error!.Kind);
        Assert.True(_popup.CanRetry);
    }

    [Fact]
    public void Unavailable_EngineAlreadyFailed_ShowsRestartHint()
    {
        _engine.OnHealthCheck = () => _engine.State = EngineState.Failed;
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);

        _translator.Fail(0, new EngineException(EngineErrorKind.Unavailable, "refused"));

        Assert.Equal(PopupErrorMapper.EngineFailedMessage, _popup.ErrorMessage);
        Assert.False(_flow.IsWaitingForEngine);
    }

    // ---- 最新优先 ----

    [Fact]
    public void NewRequest_CancelsOld_AndOldResultDoesNotOverwrite()
    {
        _flow.OnTextCaptured("first", ClipboardTrigger.Monitor);
        _flow.OnTextCaptured("second", ClipboardTrigger.Monitor);

        Assert.True(_translator.Calls[0].Token.IsCancellationRequested);
        Assert.False(_translator.Calls[1].Token.IsCancellationRequested);

        FakeTranslationService.Inline(() => _translator.Calls[0].Completion.TrySetResult(FakeTranslationService.Outcome("旧")));
        Assert.Equal(PopupKind.Loading, _popup.Kind);

        _translator.Complete(1, "新");
        Assert.Equal("新", _popup.Translation);
        Assert.Single(_completed);
    }

    [Fact]
    public void OldRequestCompletingAfterNew_IsDiscarded()
    {
        // 翻译服务不响应取消（例如结果已在路上）：旧结果晚到也不能覆盖新结果。
        var stubborn = new StubbornTranslator();
        using var popup = new PopupViewModel(timeProvider: _time);
        using var flow = new TranslateFlowCoordinator(stubborn, _engine, popup, _tray, timeProvider: _time);

        flow.OnTextCaptured("first", ClipboardTrigger.Monitor);
        flow.OnTextCaptured("second", ClipboardTrigger.Hotkey);
        FakeTranslationService.Inline(() => stubborn.Pending[1].SetResult(FakeTranslationService.Outcome("新")));
        FakeTranslationService.Inline(() => stubborn.Pending[0].SetResult(FakeTranslationService.Outcome("旧")));

        Assert.Equal("新", popup.Translation);
    }

    [Fact]
    public void OldErrorAfterNewRequest_IsDiscarded()
    {
        var stubborn = new StubbornTranslator();
        using var popup = new PopupViewModel(timeProvider: _time);
        using var flow = new TranslateFlowCoordinator(stubborn, _engine, popup, _tray, timeProvider: _time);

        flow.OnTextCaptured("first", ClipboardTrigger.Monitor);
        flow.OnTextCaptured("second", ClipboardTrigger.Monitor);
        FakeTranslationService.Inline(() => stubborn.Pending[0].SetException(new EngineException(EngineErrorKind.Timeout, "slow")));

        Assert.Equal(PopupKind.Loading, popup.Kind);
        FakeTranslationService.Inline(() => stubborn.Pending[1].SetResult(FakeTranslationService.Outcome("新")));
        Assert.Equal(PopupKind.Result, popup.Kind);
    }

    [Fact]
    public void NewRequestWhileWaiting_ReplacesPending()
    {
        _engine.State = EngineState.Starting;
        _flow.OnTextCaptured("first", ClipboardTrigger.Monitor);
        _flow.OnTextCaptured("second", ClipboardTrigger.Monitor);

        _engine.Raise(EngineState.Ready);

        Assert.Equal("second", Assert.Single(_translator.Calls).Text);
    }

    // ---- 重试 / 指定原文语种 ----

    [Fact]
    public void Retry_FromPopup_ResendsLastText()
    {
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);
        _translator.Fail(0, new EngineException(EngineErrorKind.Timeout, "slow"));

        _popup.RequestRetry();

        Assert.Equal(2, _translator.Calls.Count);
        Assert.Equal("Hello", _translator.Calls[1].Text);
        _translator.Complete(1);
        Assert.Equal(PopupKind.Result, _popup.Kind);
    }

    [Fact]
    public void Retry_WithoutPreviousRequest_DoesNothing()
    {
        _flow.Retry();

        Assert.Empty(_translator.Calls);
    }

    [Fact]
    public void SourceOverride_FromPopup_RetranslatesWithSource()
    {
        _flow.OnTextCaptured("漢字", ClipboardTrigger.Monitor);
        _translator.Complete(0, "汉字", "zh", "en");

        _popup.RequestSourceOverride("JA");

        var call = _translator.Calls[1];
        Assert.Equal("漢字", call.Text);
        Assert.Equal("ja", call.SourceOverride);

        // 重试沿用已指定的原文语种。
        _translator.Fail(1, new EngineException(EngineErrorKind.Timeout, "slow"));
        _popup.RequestRetry();
        Assert.Equal("ja", _translator.Calls[2].SourceOverride);
    }

    [Fact]
    public void SourceOverride_UnsupportedLanguage_Ignored()
    {
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);

        _flow.TranslateWithSource("fr");

        Assert.Single(_translator.Calls);
    }

    // ---- 暂停 ----

    [Fact]
    public void Paused_MonitorIgnored_HotkeyAndTrayStillTranslate()
    {
        _tray.SetPaused(true);

        _flow.OnTextCaptured("auto", ClipboardTrigger.Monitor);
        Assert.Empty(_translator.Calls);
        Assert.Equal(PopupKind.None, _popup.Kind);

        _flow.OnTextCaptured("manual", ClipboardTrigger.Hotkey);
        _flow.TranslateClipboard(ClipboardReadResult.FromText("tray"));

        Assert.Equal(["manual", "tray"], _translator.Calls.Select(c => c.Text));
    }

    // ---- 过长文本 ----

    [Fact]
    public void Monitor_TooLong_NotifiesTrayOnce()
    {
        _flow.OnTextRejected(RejectReason.TooLong, 6000, ClipboardTrigger.Monitor);
        _flow.OnTextRejected(RejectReason.TooLong, 7000, ClipboardTrigger.Monitor);

        Assert.Equal(["文本过长（6000 字），可用快捷键手动翻译"], _notifications);
        Assert.Equal(PopupKind.None, _popup.Kind);
    }

    [Fact]
    public void Monitor_TooLongWhilePaused_NoNotification()
    {
        _tray.SetPaused(true);

        _flow.OnTextRejected(RejectReason.TooLong, 6000, ClipboardTrigger.Monitor);

        Assert.Empty(_notifications);
    }

    [Theory]
    [InlineData(RejectReason.Empty)]
    [InlineData(RejectReason.Duplicate)]
    [InlineData(RejectReason.SymbolsOnly)]
    public void OtherRejections_AreSilent(RejectReason reason)
    {
        _flow.OnTextRejected(reason, 3, ClipboardTrigger.Monitor);
        _flow.OnTextRejected(reason, 3, ClipboardTrigger.Hotkey);

        Assert.Empty(_notifications);
        Assert.Equal(PopupKind.None, _popup.Kind);
    }

    [Fact]
    public void Hotkey_TooLong_ShowsTextTooLongError()
    {
        _flow.OnTextRejected(RejectReason.TooLong, 12000, ClipboardTrigger.Hotkey);

        Assert.Equal(PopupErrorKind.TextTooLong, _popup.Error!.Kind);
        Assert.Equal("文本过长：12000 字，上限 10000 字", _popup.ErrorMessage);
        Assert.False(_popup.CanRetry);
    }

    // ---- 托盘「翻译剪贴板」 ----

    [Fact]
    public void TranslateClipboard_Text_TranslatesWithTrayTrigger()
    {
        _flow.TranslateClipboard(ClipboardReadResult.FromText("  Hello  "));
        _translator.Complete(0);

        Assert.Equal("Hello", _translator.Calls[0].Text);
        Assert.Equal(ClipboardTrigger.Tray, Assert.Single(_completed).Trigger);
    }

    [Fact]
    public void TranslateClipboard_AllowsUpTo10000Chars()
    {
        _flow.TranslateClipboard(ClipboardReadResult.FromText(new string('x', 10000)));

        Assert.Single(_translator.Calls);
    }

    [Fact]
    public void TranslateClipboard_TooLong_ShowsError()
    {
        _flow.TranslateClipboard(ClipboardReadResult.FromText(new string('a', 10001)));

        Assert.Empty(_translator.Calls);
        Assert.Equal("文本过长：10001 字，上限 10000 字", _popup.ErrorMessage);
    }

    [Fact]
    public void TranslateClipboard_UnreadableOrEmpty_Notifies()
    {
        _flow.TranslateClipboard(ClipboardReadResult.Busy);
        _flow.TranslateClipboard(ClipboardReadResult.PrivateContent);
        _flow.TranslateClipboard(ClipboardReadResult.NoText);
        _flow.TranslateClipboard(ClipboardReadResult.FromText("12345 !!"));

        Assert.Equal(
            ["剪贴板被其他程序占用，请稍后再试", "剪贴板内容被标记为隐私，未读取", "剪贴板里没有文本", "剪贴板里没有可翻译的文字"],
            _notifications);
        Assert.Empty(_translator.Calls);
        Assert.Equal(PopupKind.None, _popup.Kind);
    }

    // ---- 关闭 / 释放 ----

    [Fact]
    public void UserClosesPopup_CancelsInFlight()
    {
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _time.Advance(_popup.Options.LoadingIndicatorDelay); // 浮窗出现

        _popup.Close(PopupCloseReason.User);

        Assert.True(_translator.Calls[0].Token.IsCancellationRequested);
        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public void UserClosesPreparingPopup_DropsPending()
    {
        _engine.State = EngineState.Starting;
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);

        _popup.Close(PopupCloseReason.User);
        _engine.Raise(EngineState.Ready);

        Assert.Empty(_translator.Calls);
    }

    [Fact]
    public void Dispose_CancelsAndIgnoresFurtherEvents()
    {
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);

        _flow.Dispose();
        _flow.Dispose();

        Assert.True(_translator.Calls[0].Token.IsCancellationRequested);
        _flow.OnTextCaptured("again", ClipboardTrigger.Hotkey);
        _popup.RequestSourceOverride("en");
        Assert.Single(_translator.Calls);
    }

    [Fact]
    public void Dispatch_IsUsedForBackgroundCallbacks()
    {
        var queue = new Queue<Action>();
        using var popup = new PopupViewModel(timeProvider: _time);
        using var flow = new TranslateFlowCoordinator(_translator, _engine, popup, _tray, timeProvider: _time, dispatch: queue.Enqueue);

        flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _translator.Complete(0);
        Assert.Equal(PopupKind.Loading, popup.Kind);

        queue.Dequeue()();
        Assert.Equal(PopupKind.Result, popup.Kind);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new TranslateFlowCoordinator(null!, _engine, _popup, _tray));
        Assert.Throws<ArgumentNullException>(() => new TranslateFlowCoordinator(_translator, null!, _popup, _tray));
        Assert.Throws<ArgumentNullException>(() => new TranslateFlowCoordinator(_translator, _engine, null!, _tray));
        Assert.Throws<ArgumentNullException>(() => new TranslateFlowCoordinator(_translator, _engine, _popup, null!));
    }

    /// <summary>不理会取消令牌的翻译服务。</summary>
    private sealed class StubbornTranslator : ITranslationService
    {
        public List<TaskCompletionSource<TranslationOutcome>> Pending { get; } = [];

        public Task<TranslationOutcome> TranslateAsync(string text, string? sourceOverride = null, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<TranslationOutcome>();
            Pending.Add(tcs);
            return tcs.Task;
        }

        public void CancelCurrent()
        {
        }
    }
}
