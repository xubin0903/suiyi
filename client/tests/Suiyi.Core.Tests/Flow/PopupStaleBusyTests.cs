using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Capture;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Engine;
using Suiyi.Core.Flow;
using Suiyi.Core.Popup;
using Suiyi.Core.Tests.Clipboard;
using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Flow;

/// <summary>
/// #71：忙碌态（正在翻译 / 正在识别 / 正在准备）时关闭浮窗，托盘左键重新显示不能停在忙碌态。
/// 复制翻译与框选翻译各测一遍；<see cref="Defer"/> 模拟「结果已排进 UI 线程队列，但用户的关闭先被处理」的竞态。
/// </summary>
public sealed class PopupStaleBusyTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 7, 7, 7];

    private readonly FakeTimeProvider _time = new();
    private readonly FakeTranslationService _translator = new();
    private readonly FakeOcrService _ocr = new();
    private readonly FakeRegionCapture _capture = new();
    private readonly FakeEngineStatus _engine = new();
    private readonly TrayController _tray = new();
    private readonly RecordingLogger _logger = new();
    private readonly Queue<Action> _queued = new();
    private readonly PopupViewModel _popup;
    private readonly RegionCaptureTrigger _region;
    private readonly TranslateFlowCoordinator _flow;
    private int _shown;
    private bool _defer;

    public PopupStaleBusyTests()
    {
        _popup = new PopupViewModel(timeProvider: _time);
        _popup.Shown += (_, _) => _shown++;
        _region = new RegionCaptureTrigger(_capture, _logger, _time);
        _flow = new TranslateFlowCoordinator(
            _translator,
            _engine,
            _popup,
            _tray,
            _logger,
            _time,
            dispatch: a =>
            {
                if (_defer)
                {
                    _queued.Enqueue(a);
                }
                else
                {
                    a();
                }
            },
            ocr: _ocr,
            region: _region);
    }

    public void Dispose()
    {
        _flow.Dispose();
        _popup.Dispose();
    }

    private static bool IsBusy(PopupViewModel popup) => popup.Kind is PopupKind.Loading or PopupKind.Preparing;

    /// <summary>之后的 UI 线程回调先排队，直到 <see cref="Flush"/>。</summary>
    private void Defer() => _defer = true;

    private void Flush()
    {
        _defer = false;
        while (_queued.TryDequeue(out var action))
        {
            action();
        }
    }

    private async Task StartAsync(PopupContentMode mode, bool waitVisible = true)
    {
        if (mode == PopupContentMode.Ocr)
        {
            var run = _flow.TranslateRegionAsync();
            _capture.Complete(FakeRegionCapture.Sample(Png));
            await run;
        }
        else
        {
            _flow.OnTextCaptured("Hello world", ClipboardTrigger.Hotkey);
        }

        if (waitVisible)
        {
            _time.Advance(_popup.Options.LoadingIndicatorDelay); // 加载指示出现，浮窗可见
            Assert.True(_popup.IsVisible);
        }
    }

    private int CallCount(PopupContentMode mode) => mode == PopupContentMode.Ocr ? _ocr.Calls.Count : _translator.Calls.Count;

    private CancellationToken Token(PopupContentMode mode, int index) =>
        mode == PopupContentMode.Ocr ? _ocr.Calls[index].Token : _translator.Calls[index].Token;

    private void Complete(PopupContentMode mode, int index)
    {
        if (mode == PopupContentMode.Ocr)
        {
            _ocr.Complete(index);
        }
        else
        {
            _translator.Complete(index, "你好世界");
        }
    }

    private void Fail(PopupContentMode mode, int index, EngineException ex)
    {
        if (mode == PopupContentMode.Ocr)
        {
            _ocr.Fail(index, ex);
        }
        else
        {
            _translator.Fail(index, ex);
        }
    }

    private void AssertResult(PopupContentMode mode)
    {
        Assert.Equal(PopupKind.Result, _popup.Kind);
        Assert.Equal(mode, _popup.Mode);
        Assert.Equal(mode == PopupContentMode.Ocr ? "Secret one\n\nSecret two" : "你好世界", _popup.Translation);
    }

    private void AssertCancelledRetryable(PopupContentMode mode)
    {
        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupErrorKind.Cancelled, _popup.Error!.Kind);
        Assert.Equal(mode, _popup.Mode);
        Assert.True(_popup.CanRetry);
        Assert.Contains("重试", _popup.ErrorMessage, StringComparison.Ordinal);
    }

    // ---- 关闭即取消：重显「已取消」+ 重试成功 ----

    [Theory]
    [InlineData(PopupContentMode.Text)]
    [InlineData(PopupContentMode.Ocr)]
    public async Task CloseWhileLoading_CancelsRequest_ShowLastShowsCancelled_RetrySucceeds(PopupContentMode mode)
    {
        await StartAsync(mode);

        _popup.Close(PopupCloseReason.User);

        Assert.True(Token(mode, 0).IsCancellationRequested);
        Assert.False(_popup.IsVisible);
        Assert.False(IsBusy(_popup));

        Assert.True(_popup.ShowLast()); // 托盘左键
        Assert.True(_popup.IsVisible);
        AssertCancelledRetryable(mode);
        if (mode == PopupContentMode.Ocr)
        {
            Assert.Equal(new PopupRect(200, 150, 640, 180), _popup.AnchorRect); // 仍在原选区旁
        }

        _popup.RequestRetry();

        Assert.Equal(2, CallCount(mode));
        if (mode == PopupContentMode.Ocr)
        {
            Assert.True(_ocr.Calls[1].Png.Span.SequenceEqual(Png)); // 同一张截图，不重新框选
            Assert.Equal(1, _capture.Calls);
        }
        else
        {
            Assert.Equal("Hello world", _translator.Calls[1].Text); // 原文
        }

        Assert.Equal(PopupKind.Loading, _popup.Kind);
        Complete(mode, 1);
        AssertResult(mode);
        Assert.Equal(0, _engine.Restarts);
    }

    [Theory]
    [InlineData(PopupContentMode.Text)]
    [InlineData(PopupContentMode.Ocr)]
    public async Task CloseWhilePreparing_ReadyDoesNotRun_ShowLastShowsCancelled_RetrySucceeds(PopupContentMode mode)
    {
        _engine.State = EngineState.Starting;
        await StartAsync(mode, waitVisible: false);
        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.True(_popup.IsVisible);

        _popup.Close(PopupCloseReason.User);
        _engine.Raise(EngineState.Ready);
        _time.Advance(TimeSpan.FromSeconds(60)); // 等待上限过了也不会再改浮窗

        Assert.Equal(0, CallCount(mode));
        Assert.False(_popup.IsVisible);
        Assert.True(_popup.ShowLast());
        AssertCancelledRetryable(mode);

        _popup.RequestRetry();
        Complete(mode, 0);

        AssertResult(mode);
    }

    [Fact]
    public async Task ClosedDuringUnavailableConfirm_ShowLastShowsCancelled()
    {
        await StartAsync(PopupContentMode.Text);
        _translator.Fail(0, new EngineException(EngineErrorKind.Unavailable, "refused"));
        Assert.Equal(PopupKind.Preparing, _popup.Kind);

        _popup.Close(PopupCloseReason.User);
        _time.Advance(TimeSpan.FromSeconds(60));

        Assert.True(_popup.ShowLast());
        AssertCancelledRetryable(PopupContentMode.Text);
    }

    // ---- 关闭时结果已到 / 请求没能取消：重显结果 ----

    [Theory]
    [InlineData(PopupContentMode.Text)]
    [InlineData(PopupContentMode.Ocr)]
    public async Task ResultQueuedBeforeClose_IsKeptWithoutShowing_ShowLastShowsResult(PopupContentMode mode)
    {
        await StartAsync(mode);
        Defer();
        Complete(mode, 0); // 结果已排进 UI 线程队列……
        _popup.Close(PopupCloseReason.User); // ……但用户的关闭先被处理
        var shownBefore = _shown;

        Flush();

        Assert.False(_popup.IsVisible); // 不自己弹出来
        Assert.Equal(shownBefore, _shown);
        AssertResult(mode);
        Assert.True(_popup.ShowLast());
        Assert.True(_popup.IsVisible);
        AssertResult(mode);
    }

    [Theory]
    [InlineData(PopupContentMode.Text)]
    [InlineData(PopupContentMode.Ocr)]
    public async Task RequestStillRunningAfterClose_CompletesLater_ShowLastShowsResult(PopupContentMode mode)
    {
        _translator.IgnoreCancellation = true;
        _ocr.IgnoreCancellation = true;
        await StartAsync(mode);

        _popup.Close(PopupCloseReason.User);
        Assert.False(IsBusy(_popup));

        Complete(mode, 0);

        Assert.False(_popup.IsVisible);
        AssertResult(mode);
        Assert.True(_popup.ShowLast());
        AssertResult(mode);
    }

    [Theory]
    [InlineData(PopupContentMode.Text)]
    [InlineData(PopupContentMode.Ocr)]
    public async Task ResultArrivesAfterShowLastOfCancelled_UpdatesVisiblePopup(PopupContentMode mode)
    {
        _translator.IgnoreCancellation = true;
        _ocr.IgnoreCancellation = true;
        await StartAsync(mode);
        _popup.Close(PopupCloseReason.User);
        Assert.True(_popup.ShowLast());
        AssertCancelledRetryable(mode);

        Complete(mode, 0);

        Assert.True(_popup.IsVisible);
        AssertResult(mode);
    }

    // ---- 关闭后请求失败：重显错误 ----

    [Theory]
    [InlineData(PopupContentMode.Text)]
    [InlineData(PopupContentMode.Ocr)]
    public async Task FailureQueuedBeforeClose_ShowLastShowsError(PopupContentMode mode)
    {
        await StartAsync(mode);
        Defer();
        Fail(mode, 0, new EngineException(EngineErrorKind.Timeout, "slow"));
        _popup.Close(PopupCloseReason.User);

        Flush();

        Assert.False(_popup.IsVisible);
        Assert.True(_popup.ShowLast());
        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupErrorKind.Timeout, _popup.Error!.Kind);
        Assert.Equal(mode, _popup.Mode);
        Assert.True(_popup.CanRetry);

        _popup.RequestRetry();
        Assert.Equal(2, CallCount(mode));
    }

    [Fact]
    public async Task OcrFailureAfterClose_ShowsOcrUnavailableWithHint()
    {
        _ocr.IgnoreCancellation = true;
        await StartAsync(PopupContentMode.Ocr);
        _popup.Close(PopupCloseReason.User);

        _ocr.Fail(0, new EngineException(EngineErrorKind.OcrUnavailable, "x") { ErrorCode = "ocr_unavailable", StatusCode = 503 });

        Assert.False(_popup.IsVisible);
        Assert.True(_popup.ShowLast());
        Assert.Equal(PopupErrorKind.OcrUnavailable, _popup.Error!.Kind);
        Assert.Contains("download_ocr_models.py", _popup.ErrorMessage, StringComparison.Ordinal);
        Assert.True(_popup.CanRetry);
    }

    [Fact]
    public async Task UnavailableAfterClose_ShowsErrorWithoutWaitingOrHealthCheck()
    {
        _translator.IgnoreCancellation = true;
        await StartAsync(PopupContentMode.Text);
        _popup.Close(PopupCloseReason.User);

        _translator.Fail(0, new EngineException(EngineErrorKind.Unavailable, "refused"));

        Assert.Equal(0, _engine.HealthChecks);
        Assert.False(_flow.IsWaitingForEngine);
        Assert.Equal(PopupErrorKind.ServiceUnavailable, _popup.Error!.Kind);
        Assert.False(_popup.IsVisible);
    }

    // ---- 迟到的结果不能盖掉更新的内容 ----

    [Fact]
    public async Task LateResult_AfterNewRequest_IsIgnored()
    {
        _translator.IgnoreCancellation = true;
        await StartAsync(PopupContentMode.Text);
        _popup.Close(PopupCloseReason.User);
        _flow.OnTextCaptured("Second", ClipboardTrigger.Hotkey);

        _translator.Complete(0, "旧结果");
        Assert.Equal(PopupKind.Loading, _popup.Kind);

        _translator.Complete(1, "新结果");
        Assert.Equal("新结果", _popup.Translation);
    }

    [Fact]
    public async Task LateResult_AfterRetry_IsIgnored()
    {
        _translator.IgnoreCancellation = true;
        await StartAsync(PopupContentMode.Text);
        _popup.Close(PopupCloseReason.User);
        Assert.True(_popup.ShowLast());
        _popup.RequestRetry();

        _translator.Complete(0, "旧结果");

        Assert.Equal(PopupKind.Loading, _popup.Kind); // 等重试的那次
        _translator.Complete(1, "你好世界");
        AssertResult(PopupContentMode.Text);
    }

    // ---- 新的框选隐藏了忙碌的浮窗，随后取消框选 ----

    [Theory]
    [InlineData(PopupContentMode.Text)]
    [InlineData(PopupContentMode.Ocr)]
    public async Task NewRegionWhileBusy_ThenEsc_ShowLastShowsCancelledOldRequest_RetryUsesIt(PopupContentMode mode)
    {
        await StartAsync(mode);

        var run = _flow.TranslateRegionAsync();
        Assert.True(Token(mode, 0).IsCancellationRequested);
        Assert.False(_popup.IsVisible);
        _capture.Complete(null); // 框选时按 Esc
        await run;

        Assert.True(_popup.ShowLast());
        AssertCancelledRetryable(mode);

        _popup.RequestRetry();
        Assert.Equal(2, CallCount(mode));
        if (mode == PopupContentMode.Ocr)
        {
            Assert.True(_ocr.Calls[1].Png.Span.SequenceEqual(Png)); // 旧截图
        }
        else
        {
            Assert.Equal("Hello world", _translator.Calls[1].Text);
        }

        Complete(mode, 1);
        AssertResult(mode);
    }

    // ---- 不是编排器取消的 OperationCanceledException ----

    [Theory]
    [InlineData(PopupContentMode.Text)]
    [InlineData(PopupContentMode.Ocr)]
    public async Task ForeignCancellation_ShowsTimeoutInsteadOfStayingBusy(PopupContentMode mode)
    {
        await StartAsync(mode);

        if (mode == PopupContentMode.Ocr)
        {
            FakeTranslationService.Inline(() => _ocr.Calls[0].Completion.SetCanceled());
        }
        else
        {
            FakeTranslationService.Inline(() => _translator.Calls[0].Completion.SetCanceled());
        }

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupErrorKind.Timeout, _popup.Error!.Kind);
        Assert.True(_popup.CanRetry);
    }

    // ---- 其他关闭方式与状态 ----

    [Fact]
    public async Task CloseAfterResult_KeepsResult()
    {
        await StartAsync(PopupContentMode.Text);
        Complete(PopupContentMode.Text, 0);
        _popup.Close(PopupCloseReason.User);

        Assert.True(_popup.ShowLast());
        AssertResult(PopupContentMode.Text);
    }

    [Fact]
    public async Task Close_LogsCancellation()
    {
        await StartAsync(PopupContentMode.Ocr);
        _popup.Close(PopupCloseReason.User);

        Assert.Contains(_logger.Messages, m => m.Contains("关闭浮窗，请求已取消", StringComparison.Ordinal) && m.Contains("Ocr", StringComparison.Ordinal));
    }
}
