using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Capture;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Engine;
using Suiyi.Core.Flow;
using Suiyi.Core.Popup;
using Suiyi.Core.Tests.Clipboard;
using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Flow;

/// <summary>框选翻译主流程（#58）：假框选、假 OCR 服务、真浮窗 ViewModel。</summary>
public sealed class RegionTranslateFlowTests : IDisposable
{
    private static readonly PopupRect SampleAnchor = new(200, 150, 640, 180);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeTranslationService _translator = new();
    private readonly FakeOcrService _ocr = new();
    private readonly FakeRegionCapture _capture = new();
    private readonly FakeEngineStatus _engine = new();
    private readonly TrayController _tray = new();
    private readonly RecordingLogger _logger = new();
    private readonly List<string> _notifications = [];
    private readonly List<RegionTranslateCompletedEventArgs> _completed = [];
    private readonly PopupViewModel _popup;
    private readonly RegionCaptureTrigger _region;
    private readonly TranslateFlowCoordinator _flow;

    public RegionTranslateFlowTests()
    {
        _popup = new PopupViewModel(timeProvider: _time);
        _tray.NotificationRequested += (_, e) => _notifications.Add(e.Message);
        _region = new RegionCaptureTrigger(_capture, _logger, _time);
        _flow = new TranslateFlowCoordinator(_translator, _engine, _popup, _tray, _logger, _time, ocr: _ocr, region: _region);
        _flow.RegionCompleted += (_, e) => _completed.Add(e);
    }

    public void Dispose()
    {
        _flow.Dispose();
        _popup.Dispose();
    }

    /// <summary>触发框选并完成选区，返回框选 PNG。</summary>
    private async Task<byte[]> SelectAsync(byte[]? png = null, RegionTranslateTrigger trigger = RegionTranslateTrigger.Hotkey)
    {
        png ??= [0x89, 0x50, 0x4E, 0x47, 9, 9, 9];
        var run = _flow.TranslateRegionAsync(trigger);
        _capture.Complete(FakeRegionCapture.Sample(png));
        await run;
        return png;
    }

    private static EngineException Error(EngineErrorKind kind, string? code = null) => new(kind, "x") { ErrorCode = code };

    // ---- 成功 / 空结果 ----

    [Fact]
    public async Task Success_ShowsOcrLoadingAroundSelection_ThenResult()
    {
        var png = await SelectAsync();

        Assert.Equal(PopupKind.Loading, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.Equal(SampleAnchor, _popup.AnchorRect);
        var call = Assert.Single(_ocr.Calls);
        Assert.True(call.Png.Span.SequenceEqual(png));

        _ocr.Complete(0);

        Assert.Equal(PopupKind.Result, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.Equal(SampleAnchor, _popup.AnchorRect);
        Assert.Equal("Secret one\n\nSecret two", _popup.Translation);
        Assert.Equal("机密第一段\n\n机密第二段", _popup.OriginalText);
        Assert.Equal("300 ms", _popup.ElapsedText);
        Assert.Empty(_translator.Calls);
        Assert.False(_flow.HasRetainedScreenshot);
    }

    [Fact]
    public async Task Success_RecordsOcrE2eFromSelectionReleased_AndRaisesCompleted()
    {
        var run = _flow.TranslateRegionAsync(RegionTranslateTrigger.Tray);
        _time.Advance(TimeSpan.FromSeconds(3)); // 用户拖拽时间不计入
        _capture.Complete(FakeRegionCapture.Sample());
        await run;
        _time.Advance(TimeSpan.FromMilliseconds(420));
        _ocr.Complete(0);

        var done = Assert.Single(_completed);
        Assert.Equal(RegionTranslateTrigger.Tray, done.Trigger);
        Assert.Equal(TimeSpan.FromMilliseconds(420), done.EndToEnd);
        Assert.False(done.WaitedForEngine);
        Assert.Equal(1, _flow.OcrLatency.Count);
        Assert.Equal(420, _flow.OcrLatency.Percentile(95));
        Assert.Equal(0, _flow.Latency.Count); // 与复制翻译分开统计
    }

    [Fact]
    public async Task Success_LogsSizesAndTimings_ButNoRecognizedText()
    {
        await SelectAsync();
        _ocr.Complete(0);

        var line = Assert.Single(_logger.Messages, m => m.Contains("框选翻译完成", StringComparison.Ordinal));
        Assert.Contains("size=640x180", line, StringComparison.Ordinal);
        Assert.Contains("paragraphs=2", line, StringComparison.Ordinal);
        Assert.Contains("ocr_e2e_ms=0", line, StringComparison.Ordinal);
        Assert.Contains("server_ocr_ms=210", line, StringComparison.Ordinal);
        Assert.Contains("server_translate_ms=40", line, StringComparison.Ordinal);
        Assert.Contains("P95", line, StringComparison.Ordinal);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("机密", StringComparison.Ordinal) || m.Contains("Secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyRecognition_ShowsEmptyNotError()
    {
        await SelectAsync();
        _ocr.Complete(0, FakeOcrService.Empty);

        Assert.Equal(PopupKind.Empty, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.False(_popup.CanRetry);
        Assert.Contains(_logger.Messages, m => m.Contains("empty=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartialTranslation_ShowsUntranslatedHint()
    {
        await SelectAsync();
        _ocr.Complete(0, """{"paragraphs":[{"text":"A"},{"text":"B"}],"translation":{"results":[{"text":"甲","source":"en","detected":true,"target":"zh","route":[],"elapsed_ms":1}]}}""", "zh");

        Assert.Equal("第 2 段未能翻译，显示为原文", _popup.UntranslatedHint);
        Assert.Contains(_logger.Messages, m => m.Contains("untranslated=1", StringComparison.Ordinal));
    }

    // ---- OCR 错误 ----

    [Theory]
    [InlineData(EngineErrorKind.ImageTooLarge, "image_too_large", PopupErrorKind.ImageTooLarge, false)]
    [InlineData(EngineErrorKind.UnsupportedMediaType, "unsupported_media_type", PopupErrorKind.InvalidImage, true)]
    [InlineData(EngineErrorKind.InvalidImage, "invalid_image", PopupErrorKind.InvalidImage, true)]
    [InlineData(EngineErrorKind.OcrUnavailable, "ocr_unavailable", PopupErrorKind.OcrUnavailable, true)]
    [InlineData(EngineErrorKind.Timeout, null, PopupErrorKind.Timeout, true)]
    [InlineData(EngineErrorKind.DetectFailed, "detect_failed", PopupErrorKind.DetectFailed, true)]
    [InlineData(EngineErrorKind.UnsupportedPair, "unsupported_pair", PopupErrorKind.MissingModels, true)]
    [InlineData(EngineErrorKind.Internal, "internal_error", PopupErrorKind.Other, true)]
    public async Task OcrErrors_ShowInOcrMode_AroundSelection(EngineErrorKind kind, string? code, PopupErrorKind expected, bool canRetry)
    {
        await SelectAsync();
        _ocr.Fail(0, Error(kind, code));

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(expected, _popup.Error!.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.Equal(SampleAnchor, _popup.AnchorRect);
        Assert.Equal(canRetry, _popup.CanRetry);
        Assert.True(_flow.HasRetainedScreenshot); // 供重试
    }

    [Fact]
    public async Task OcrUnavailableWithModels_ShowsModelNames()
    {
        await SelectAsync();
        _ocr.Fail(0, new EngineException(EngineErrorKind.OcrUnavailable, "x") { ErrorCode = "ocr_unavailable", MissingModels = ["PP-OCRv6_det_small"] });

        Assert.Equal("OCR 模型未安装：PP-OCRv6_det_small", _popup.ErrorMessage);
    }

    [Fact]
    public async Task ClientPrecheckTooLarge_ShowsImageTooLarge()
    {
        await SelectAsync();
        _ocr.Fail(0, EngineClient.PrecheckImage(new byte[9 * 1024 * 1024])!);

        Assert.Equal(PopupErrorKind.ImageTooLarge, _popup.Error!.Kind);
        Assert.False(_popup.CanRetry);
    }

    [Fact]
    public async Task UnexpectedException_ShowsOther_AndLogsNoMessage()
    {
        await SelectAsync();
        _ocr.Fail(0, new InvalidOperationException("机密 boom"));

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("机密", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OcrFailure_LogsCodeNotServerMessage()
    {
        await SelectAsync();
        _ocr.Fail(0, new EngineException(EngineErrorKind.InvalidImage, "ocr_translate 返回 HTTP 422 invalid_image：机密说明") { ErrorCode = "invalid_image", StatusCode = 422 });

        var line = Assert.Single(_logger.Messages, m => m.Contains("框选翻译失败", StringComparison.Ordinal));
        Assert.Contains("code=invalid_image", line, StringComparison.Ordinal);
        Assert.Contains("status=422", line, StringComparison.Ordinal);
        Assert.DoesNotContain("机密", line, StringComparison.Ordinal);
    }

    // ---- 重试按 Mode 分流 ----

    [Fact]
    public async Task Retry_InOcrMode_ReusesSamePng_WithoutNewSelection()
    {
        var png = await SelectAsync();
        _ocr.Fail(0, Error(EngineErrorKind.Timeout));

        _popup.RequestRetry();

        Assert.Equal(1, _capture.Calls);
        Assert.Equal(2, _ocr.Calls.Count);
        Assert.True(_ocr.Calls[1].Png.Span.SequenceEqual(png));
        Assert.Empty(_translator.Calls);
        Assert.Equal(PopupKind.Loading, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);

        _ocr.Complete(1);
        Assert.Equal(PopupKind.Result, _popup.Kind);
        Assert.False(_flow.HasRetainedScreenshot);
    }

    [Fact]
    public async Task Retry_InOcrMode_EvenIfEarlierTextRequestExists()
    {
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _translator.Complete(0);
        await SelectAsync();
        _ocr.Fail(0, Error(EngineErrorKind.InvalidImage, "invalid_image"));

        _popup.RequestRetry();

        Assert.Single(_translator.Calls);
        Assert.Equal(2, _ocr.Calls.Count);
    }

    [Fact]
    public async Task Retry_InTextMode_RetranslatesText_EvenAfterEarlierOcr()
    {
        await SelectAsync();
        _ocr.Complete(0);
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _translator.Fail(0, Error(EngineErrorKind.Timeout));

        Assert.Equal(PopupContentMode.Text, _popup.Mode);
        _popup.RequestRetry();

        Assert.Equal(2, _translator.Calls.Count);
        Assert.Equal("Hello", _translator.Calls[1].Text);
        Assert.Single(_ocr.Calls);
    }

    [Fact]
    public async Task EngineFailedForTextAfterOcrPopup_ErrorIsTextMode_RetryIsText()
    {
        await SelectAsync();
        _ocr.Fail(0, Error(EngineErrorKind.Timeout));
        _engine.State = EngineState.Failed;

        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupContentMode.Text, _popup.Mode);
        Assert.Null(_popup.AnchorRect);
        Assert.False(_flow.HasRetainedScreenshot);
    }

    [Fact]
    public async Task Retry_AfterPopupClosed_ScreenshotDiscarded_NoOp()
    {
        await SelectAsync();
        _ocr.Fail(0, Error(EngineErrorKind.Timeout));
        _popup.Close(PopupCloseReason.User);

        Assert.False(_flow.HasRetainedScreenshot);
        _flow.Retry();

        Assert.Single(_ocr.Calls);
    }

    [Fact]
    public async Task AutoHideAfterError_DiscardsScreenshot()
    {
        await SelectAsync();
        _ocr.Fail(0, Error(EngineErrorKind.Timeout));
        _popup.Close(PopupCloseReason.AutoHide);

        Assert.False(_flow.HasRetainedScreenshot);
    }

    // ---- 服务未就绪 / 重启 ----

    [Fact]
    public async Task EngineStarting_ShowsPreparingAroundSelection_ThenAutoContinues()
    {
        _engine.State = EngineState.Starting;
        var png = await SelectAsync();

        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.Equal(SampleAnchor, _popup.AnchorRect);
        Assert.Empty(_ocr.Calls);
        Assert.True(_flow.IsWaitingForEngine);

        _time.Advance(TimeSpan.FromSeconds(4));
        _engine.Raise(EngineState.Ready);

        var call = Assert.Single(_ocr.Calls);
        Assert.True(call.Png.Span.SequenceEqual(png));
        _ocr.Complete(0);
        Assert.Equal(PopupKind.Result, _popup.Kind);
        Assert.True(Assert.Single(_completed).WaitedForEngine);
        Assert.Equal(0, _flow.OcrLatency.Count); // 等过服务，不计入统计
    }

    [Fact]
    public async Task EngineNeverReady_TimesOutAfter30s_InOcrMode_RetryRestartsAndReusesPng()
    {
        _engine.State = EngineState.Starting;
        await SelectAsync();

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupErrorKind.EngineStartTimeout, _popup.Error!.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);

        _popup.RequestRetry();

        Assert.Equal(1, _engine.Restarts);
        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        _engine.Raise(EngineState.Ready);
        Assert.Single(_ocr.Calls);
        Assert.Equal(1, _capture.Calls);
    }

    [Fact]
    public async Task EngineFailed_ShowsErrorInOcrMode_RetryRestarts()
    {
        _engine.State = EngineState.Failed;
        await SelectAsync();

        Assert.Equal(PopupKind.Error, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        Assert.Equal(SampleAnchor, _popup.AnchorRect);

        _popup.RequestRetry();

        Assert.Equal(1, _engine.Restarts);
        _engine.Raise(EngineState.Ready);
        Assert.Single(_ocr.Calls);
    }

    [Fact]
    public async Task ConnectionRefused_HealthCheck_ThenRerunsOcrWhenReady()
    {
        _engine.OnHealthCheck = () => _engine.Raise(EngineState.Restarting);
        await SelectAsync();

        _ocr.Fail(0, Error(EngineErrorKind.Unavailable));

        Assert.Equal(1, _engine.HealthChecks);
        Assert.Equal(PopupKind.Preparing, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
        _engine.Raise(EngineState.Ready);

        Assert.Equal(2, _ocr.Calls.Count);
        _ocr.Complete(1);
        Assert.Equal(PopupKind.Result, _popup.Kind);
    }

    // ---- 互相取消 ----

    [Fact]
    public async Task RegionStart_CancelsInFlightText_AndHidesPopup()
    {
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _time.Advance(TimeSpan.FromSeconds(1)); // 浮窗已显示
        Assert.True(_popup.IsVisible);

        var run = _flow.TranslateRegionAsync();

        Assert.True(_translator.Calls[0].Token.IsCancellationRequested);
        Assert.False(_popup.IsVisible);

        _capture.Complete(FakeRegionCapture.Sample());
        await run;
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
    }

    [Fact]
    public async Task TextCapture_CancelsInFlightOcr_LateOcrResultIgnored()
    {
        await SelectAsync();

        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);

        Assert.True(_ocr.Calls[0].Token.IsCancellationRequested);
        Assert.Equal(PopupContentMode.Text, _popup.Mode);
        Assert.False(_flow.HasRetainedScreenshot);

        _translator.Complete(0, "你好");
        Assert.Equal("你好", _popup.Translation);
        Assert.Equal(PopupContentMode.Text, _popup.Mode);
        Assert.Empty(_completed);
    }

    [Fact]
    public async Task TrayTranslateClipboard_CancelsInFlightOcr()
    {
        await SelectAsync();

        _flow.TranslateClipboard(new ClipboardReadResult(ClipboardReadStatus.Text, "Hello"));

        Assert.True(_ocr.Calls[0].Token.IsCancellationRequested);
        Assert.Single(_translator.Calls);
    }

    [Fact]
    public async Task NewRegion_CancelsPreviousOcr()
    {
        await SelectAsync();
        await SelectAsync([0x89, 0x50, 0x4E, 0x47, 7]);

        Assert.True(_ocr.Calls[0].Token.IsCancellationRequested);
        Assert.Equal(2, _ocr.Calls.Count);
        _ocr.Complete(1);
        Assert.Equal(PopupKind.Result, _popup.Kind);
    }

    [Fact]
    public async Task NewRegion_WhileWaitingForEngine_ReplacesPendingOcr()
    {
        _engine.State = EngineState.Starting;
        await SelectAsync([0x89, 1]);
        await SelectAsync([0x89, 2]);

        _engine.Raise(EngineState.Ready);

        var call = Assert.Single(_ocr.Calls);
        Assert.Equal(2, call.Png.Span[1]);
    }

    [Fact]
    public async Task TextDuringOverlay_Ignored_OverlayResultStillShown()
    {
        var run = _flow.TranslateRegionAsync();

        _flow.OnTextCaptured("Hello", ClipboardTrigger.Monitor);
        _flow.OnTextCaptured("Hello", ClipboardTrigger.Hotkey);
        _flow.OnTextRejected(RejectReason.TooLong, 20000, ClipboardTrigger.Hotkey);
        _flow.TranslateClipboard(new ClipboardReadResult(ClipboardReadStatus.Text, "Hello"));

        Assert.Empty(_translator.Calls);
        Assert.False(_popup.IsVisible);

        _capture.Complete(FakeRegionCapture.Sample());
        await run;
        Assert.Single(_ocr.Calls);
    }

    [Fact]
    public async Task RepeatedTriggerDuringOverlay_Ignored()
    {
        var first = _flow.TranslateRegionAsync(RegionTranslateTrigger.Hotkey);
        await _flow.TranslateRegionAsync(RegionTranslateTrigger.Tray);
        await _flow.TranslateRegionAsync(RegionTranslateTrigger.Hotkey);

        Assert.Equal(1, _capture.Calls);
        _capture.Complete(FakeRegionCapture.Sample());
        await first;
        Assert.Single(_ocr.Calls);
    }

    // ---- 框选取消 / 截屏异常 / 关闭浮窗 ----

    [Fact]
    public async Task SelectionCanceled_NoPopupNoRequest()
    {
        var run = _flow.TranslateRegionAsync();
        _capture.Complete(null);
        await run;

        Assert.Empty(_ocr.Calls);
        Assert.False(_popup.IsVisible);
        Assert.Equal(PopupKind.None, _popup.Kind);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public async Task SelectionCanceled_AfterPreviousResult_PopupStaysHidden()
    {
        await SelectAsync();
        _ocr.Complete(0);
        Assert.True(_popup.IsVisible);

        var run = _flow.TranslateRegionAsync();
        Assert.False(_popup.IsVisible);
        _capture.Complete(null);
        await run;

        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public async Task CaptureThrows_NoPopup_TrayNotifiesOnce()
    {
        var run = _flow.TranslateRegionAsync();
        _capture.Fail(new InvalidOperationException("GDI"));
        await run;

        Assert.Empty(_ocr.Calls);
        Assert.False(_popup.IsVisible);
        Assert.Equal(["框选截屏失败，请重试（详情见日志）"], _notifications);
    }

    [Fact]
    public async Task ExternalCancellation_DuringOverlay_NoPopup()
    {
        using var cts = new CancellationTokenSource();
        var run = _flow.TranslateRegionAsync(cancellationToken: cts.Token);
        cts.Cancel();
        await run;

        Assert.Empty(_ocr.Calls);
        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public async Task UserClosesPopupDuringOcr_CancelsRequest_LateResultIgnored()
    {
        await SelectAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_popup.IsVisible);

        _popup.Close(PopupCloseReason.User);

        Assert.True(_ocr.Calls[0].Token.IsCancellationRequested);
        Assert.False(_flow.HasRetainedScreenshot);
        Assert.False(_popup.IsVisible);
        Assert.Empty(_completed);
    }

    [Fact]
    public async Task UserClosesPopupWhileWaitingForEngine_ReadyDoesNotRun()
    {
        _engine.State = EngineState.Starting;
        await SelectAsync();

        _popup.Close(PopupCloseReason.User);
        _engine.Raise(EngineState.Ready);

        Assert.Empty(_ocr.Calls);
        Assert.False(_popup.IsVisible);
    }

    // ---- 暂停监听 / 未启用 / 释放 ----

    [Fact]
    public async Task PausedMonitoring_DoesNotAffectRegionTranslate()
    {
        _tray.SetPaused(true);

        await SelectAsync();
        _ocr.Complete(0);

        Assert.Equal(PopupKind.Result, _popup.Kind);
        Assert.Equal(PopupContentMode.Ocr, _popup.Mode);
    }

    [Fact]
    public async Task NotEnabled_WithoutOcrService_DoesNothing()
    {
        using var popup = new PopupViewModel(timeProvider: _time);
        using var flow = new TranslateFlowCoordinator(_translator, _engine, popup, _tray, _logger, _time);

        await flow.TranslateRegionAsync();

        Assert.False(flow.IsRegionTranslateEnabled);
        Assert.Equal(0, _capture.Calls);
        Assert.True(_flow.IsRegionTranslateEnabled);
    }

    [Fact]
    public async Task Disposed_IgnoresRegionAndLateResults()
    {
        await SelectAsync();
        _flow.Dispose();

        Assert.True(_ocr.Calls[0].Token.IsCancellationRequested);
        await _flow.TranslateRegionAsync();
        Assert.Equal(1, _capture.Calls);
        Assert.False(_flow.HasRetainedScreenshot);
    }

    [Fact]
    public async Task CaptureFailedAfterDispose_NoNotification()
    {
        _flow.Dispose();
        var run = _region.RunAsync();
        _capture.Fail(new InvalidOperationException("GDI"));
        await run;

        Assert.Empty(_notifications);
    }
}
