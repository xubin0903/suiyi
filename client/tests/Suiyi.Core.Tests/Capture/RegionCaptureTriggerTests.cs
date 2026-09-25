using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Capture;
using Suiyi.Core.Tests.Clipboard;

namespace Suiyi.Core.Tests.Capture;

public sealed class RegionCaptureTriggerTests
{
    private static readonly RegionCaptureResult Sample = new(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, new PixelRect(-2310, -55, 500, 100), Monitors.Left125);

    private readonly FakeRegionCapture _capture = new();
    private readonly RecordingLogger _logger = new();
    private readonly FakeTimeProvider _time = new();
    private readonly RegionCaptureTrigger _trigger;
    private readonly List<RegionCapturedEventArgs> _captured = [];

    public RegionCaptureTriggerTests()
    {
        _trigger = new RegionCaptureTrigger(_capture, _logger, _time);
        _trigger.Captured += (_, e) => _captured.Add(e);
    }

    [Fact]
    public async Task Completed_RaisesCapturedWithElapsed()
    {
        var run = _trigger.RunAsync();
        Assert.True(_trigger.IsCapturing);
        _time.Advance(TimeSpan.FromMilliseconds(1234));
        _capture.Complete(Sample);

        var result = await run;

        Assert.Same(Sample, result);
        Assert.False(_trigger.IsCapturing);
        var e = Assert.Single(_captured);
        Assert.Same(Sample, e.Result);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), e.Elapsed);
        Assert.Equal(1.25, e.Result.Scale);
    }

    [Fact]
    public async Task Completed_LogsSizeNotContent()
    {
        var run = _trigger.RunAsync();
        _capture.Complete(Sample);
        await run;

        var line = Assert.Single(_logger.Messages, m => m.Contains("框选：完成", StringComparison.Ordinal));
        Assert.Contains("500×100", line, StringComparison.Ordinal);
        Assert.Contains("(-2310,-55)", line, StringComparison.Ordinal);
        Assert.Contains("125%", line, StringComparison.Ordinal);
        Assert.Contains("DISPLAY3", line, StringComparison.Ordinal);
        Assert.Contains("PNG 4 字节", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_ReturnsNullWithoutEvent()
    {
        var run = _trigger.RunAsync();
        _capture.Complete(null);

        Assert.Null(await run);
        Assert.Empty(_captured);
        Assert.False(_trigger.IsCapturing);
        Assert.Contains(_logger.Messages, m => m.Contains("框选：已取消", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedTrigger_WhileOverlayShown_Ignored()
    {
        var first = _trigger.RunAsync();

        var second = await _trigger.RunAsync();
        var third = await _trigger.RunAsync();

        Assert.Null(second);
        Assert.Null(third);
        Assert.Equal(1, _capture.Calls);
        Assert.Equal(2, _logger.Messages.Count(m => m.Contains("忽略重复触发", StringComparison.Ordinal)));

        _capture.Complete(Sample);
        Assert.Same(Sample, await first);
    }

    [Fact]
    public async Task AfterFinish_CanCaptureAgain()
    {
        var first = _trigger.RunAsync();
        _capture.Complete(null);
        await first;

        var second = _trigger.RunAsync();
        _capture.Complete(Sample);

        Assert.Same(Sample, await second);
        Assert.Equal(2, _capture.Calls);
    }

    [Fact]
    public async Task CaptureThrows_TreatedAsCancelAndLogged()
    {
        var run = _trigger.RunAsync();
        _capture.Fail(new InvalidOperationException("BitBlt 失败"));

        Assert.Null(await run);
        Assert.False(_trigger.IsCapturing);
        Assert.Contains(_logger.Messages, m => m.StartsWith("[Error] 框选：截屏失败", StringComparison.Ordinal) && m.Contains("BitBlt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureThrows_RaisesFailedOnce_NotCaptured()
    {
        var failures = new List<Exception>();
        _trigger.Failed += (_, e) => failures.Add(e.Exception);
        var boom = new InvalidOperationException("GDI 资源不足");

        var run = _trigger.RunAsync();
        _capture.Fail(boom);

        Assert.Null(await run);
        Assert.Same(boom, Assert.Single(failures));
        Assert.Empty(_captured);
    }

    [Fact]
    public async Task UserCancel_DoesNotRaiseFailed()
    {
        var failures = 0;
        _trigger.Failed += (_, _) => failures++;

        var run = _trigger.RunAsync();
        _capture.Complete(null);

        Assert.Null(await run);
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task ExternalCancellation_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        var run = _trigger.RunAsync(cts.Token);
        Assert.Equal(cts.Token, _capture.LastToken);

        cts.Cancel();
        _capture.Fail(new OperationCanceledException(cts.Token));

        Assert.Null(await run);
        Assert.False(_trigger.IsCapturing);
        Assert.DoesNotContain(_logger.Messages, m => m.StartsWith("[Error]", StringComparison.Ordinal));
    }

    [Fact]
    public void Arguments_Validated()
    {
        Assert.Throws<ArgumentNullException>(() => new RegionCaptureTrigger(null!));
    }

    private sealed class FakeRegionCapture : IRegionCapture
    {
        private TaskCompletionSource<RegionCaptureResult?>? _pending;

        public int Calls { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public Task<RegionCaptureResult?> CaptureAsync(CancellationToken cancellationToken)
        {
            Calls++;
            LastToken = cancellationToken;
            _pending = new TaskCompletionSource<RegionCaptureResult?>();
            return _pending.Task;
        }

        public void Complete(RegionCaptureResult? result) => _pending!.SetResult(result);

        public void Fail(Exception ex) => _pending!.SetException(ex);
    }
}
