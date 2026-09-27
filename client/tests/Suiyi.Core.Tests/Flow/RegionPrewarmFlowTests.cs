using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Capture;
using Suiyi.Core.Engine;
using Suiyi.Core.Flow;
using Suiyi.Core.Popup;
using Suiyi.Core.Tests.Clipboard;
using Suiyi.Core.Tray;

namespace Suiyi.Core.Tests.Flow;

/// <summary>框选翻译触发 OCR 预热（#109）：假预热器、假框选、假 OCR 服务。</summary>
public sealed class RegionPrewarmFlowTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeTranslationService _translator = new();
    private readonly FakeOcrService _ocr = new();
    private readonly FakeRegionCapture _capture = new();
    private readonly FakeEngineStatus _engine = new();
    private readonly TrayController _tray = new();
    private readonly RecordingLogger _logger = new();
    private readonly FakePrewarmer _prewarmer;
    private readonly PopupViewModel _popup;
    private readonly RegionCaptureTrigger _region;
    private readonly TranslateFlowCoordinator _flow;

    public RegionPrewarmFlowTests()
    {
        _prewarmer = new FakePrewarmer(_capture);
        _popup = new PopupViewModel(timeProvider: _time);
        _region = new RegionCaptureTrigger(_capture, _logger, _time);
        _flow = new TranslateFlowCoordinator(_translator, _engine, _popup, _tray, _logger, _time, ocr: _ocr, region: _region, ocrPrewarmer: _prewarmer);
    }

    public void Dispose()
    {
        _flow.Dispose();
        _popup.Dispose();
    }

    [Theory]
    [InlineData(RegionTranslateTrigger.Hotkey)]
    [InlineData(RegionTranslateTrigger.Tray)]
    public async Task Trigger_PrewarmsBeforeOverlayShows(RegionTranslateTrigger trigger)
    {
        var run = _flow.TranslateRegionAsync(trigger);

        Assert.Equal(1, _prewarmer.Calls);
        Assert.Equal(0, _prewarmer.CaptureCallsAtPrewarm); // 在遮罩（CaptureAsync）之前
        Assert.Equal(1, _capture.Calls);

        _capture.Complete(FakeRegionCapture.Sample());
        await run;
        Assert.Single(_ocr.Calls); // 正式识别照常发出
        Assert.Equal(1, _prewarmer.Calls); // 选区完成后不再预热
    }

    [Fact]
    public async Task Prewarm_DoesNotBlockOverlay_EvenIfBackgroundWorkNeverFinishes()
    {
        _prewarmer.OnPrewarm = () => { }; // 假预热器立即返回；真实实现的后台任务见 OcrPrewarmerTests

        var run = _flow.TranslateRegionAsync();
        Assert.Equal(1, _capture.Calls); // 遮罩已显示
        _capture.Complete(null); // 用户 Esc
        await run;

        Assert.Empty(_ocr.Calls);
        Assert.False(_popup.IsVisible);
    }

    [Fact]
    public async Task RepeatWhileOverlayShown_DoesNotPrewarmAgain()
    {
        var run = _flow.TranslateRegionAsync();
        await _flow.TranslateRegionAsync(RegionTranslateTrigger.Tray); // 遮罩已显示，忽略

        Assert.Equal(1, _prewarmer.Calls);
        _capture.Complete(null);
        await run;
    }

    [Theory]
    [InlineData(EngineState.Starting)]
    [InlineData(EngineState.Failed)]
    public async Task EngineNotReady_DoesNotPrewarm(EngineState state)
    {
        _engine.State = state;

        var run = _flow.TranslateRegionAsync();
        _capture.Complete(null);
        await run;

        Assert.Equal(0, _prewarmer.Calls);
    }

    [Fact]
    public async Task PrewarmerThrows_RegionTranslateContinues()
    {
        _prewarmer.OnPrewarm = () => throw new InvalidOperationException("bug");

        var run = _flow.TranslateRegionAsync();
        _capture.Complete(FakeRegionCapture.Sample());
        await run;
        _ocr.Complete(0);

        Assert.Equal(PopupKind.Result, _popup.Kind);
        Assert.Contains(_logger.Messages, m => m.Contains("框选预热：触发失败（已忽略", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutPrewarmer_RegionTranslateUnchanged()
    {
        using var flow = new TranslateFlowCoordinator(_translator, _engine, _popup, _tray, _logger, _time, ocr: _ocr, region: new RegionCaptureTrigger(_capture, _logger, _time));

        var run = flow.TranslateRegionAsync();
        _capture.Complete(FakeRegionCapture.Sample());
        await run;

        Assert.Single(_ocr.Calls);
    }

    private sealed class FakePrewarmer(FakeRegionCapture capture) : IOcrPrewarmer
    {
        public int Calls { get; private set; }

        public int CaptureCallsAtPrewarm { get; private set; } = -1;

        public Action? OnPrewarm { get; set; }

        public OcrPrewarmStart Prewarm()
        {
            Calls++;
            CaptureCallsAtPrewarm = capture.Calls;
            OnPrewarm?.Invoke();
            return OcrPrewarmStart.Started;
        }
    }
}
