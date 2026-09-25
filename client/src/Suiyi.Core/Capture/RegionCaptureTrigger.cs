using System.Globalization;
using Suiyi.Core.Logging;

namespace Suiyi.Core.Capture;

/// <summary><see cref="RegionCaptureTrigger.Captured"/> 参数。</summary>
/// <param name="result">截图结果。</param>
/// <param name="elapsed">从触发到拿到 PNG 的耗时（含用户拖拽时间）。</param>
public sealed class RegionCapturedEventArgs(RegionCaptureResult result, TimeSpan elapsed) : EventArgs
{
    /// <summary>截图结果。</summary>
    public RegionCaptureResult Result { get; } = result;

    /// <summary>从触发到拿到 PNG 的耗时。</summary>
    public TimeSpan Elapsed { get; } = elapsed;
}

/// <summary><see cref="RegionCaptureTrigger.Failed"/> 参数。</summary>
/// <param name="exception">截屏时抛出的异常（已记日志）。</param>
public sealed class RegionCaptureFailedEventArgs(Exception exception) : EventArgs
{
    /// <summary>截屏时抛出的异常。</summary>
    public Exception Exception { get; } = exception;
}

/// <summary>
/// 框选入口（快捷键 <c>hotkey.region</c> 调用）：同一时刻只允许一次框选，遮罩显示期间重复触发被忽略；
/// 完成时发 <see cref="Captured"/>。日志只记尺寸、显示器、缩放和耗时，不记图片内容。异常记日志后按取消处理。
/// </summary>
public sealed class RegionCaptureTrigger
{
    private readonly IRegionCapture _capture;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _time;

    /// <summary>创建入口。</summary>
    public RegionCaptureTrigger(IRegionCapture capture, IAppLogger? logger = null, TimeProvider? timeProvider = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _logger = logger ?? NullAppLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>框选完成（未取消）。在调用 <see cref="RunAsync"/> 的线程上触发。</summary>
    public event EventHandler<RegionCapturedEventArgs>? Captured;

    /// <summary>截屏失败（GDI 资源不足等）。<see cref="RunAsync"/> 仍按取消返回 <see langword="null"/>；订阅方可据此提示用户。</summary>
    public event EventHandler<RegionCaptureFailedEventArgs>? Failed;

    /// <summary>是否正在框选（遮罩显示中）。</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>开始一次框选。已在框选中时直接返回 <see langword="null"/>，不打开第二个遮罩。</summary>
    public async Task<RegionCaptureResult?> RunAsync(CancellationToken cancellationToken = default)
    {
        if (IsCapturing)
        {
            _logger.Info("框选：遮罩已显示，忽略重复触发");
            return null;
        }

        IsCapturing = true;
        var started = _time.GetTimestamp();
        RegionCaptureResult? result;
        Exception? failure = null;
        try
        {
            result = await _capture.CaptureAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result = null;
        }
#pragma warning disable CA1031 // 截屏失败（GDI 资源不足等）不能让异常逃逸到消息循环。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.Error("框选：截屏失败", ex);
            result = null;
            failure = ex;
        }
        finally
        {
            IsCapturing = false;
        }

        var elapsed = _time.GetElapsedTime(started);
        if (failure is not null)
        {
            Failed?.Invoke(this, new RegionCaptureFailedEventArgs(failure));
            return null;
        }

        if (result is null)
        {
            _logger.Info("框选：已取消");
            return null;
        }

        _logger.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"框选：完成 {result.Bounds.Width}×{result.Bounds.Height} 物理像素，位置 {result.Bounds}，显示器 {result.Monitor.DeviceName}（缩放 {result.Scale * 100:0}%），PNG {result.Png.Length} 字节，耗时 {elapsed.TotalMilliseconds:0} ms"));
        Captured?.Invoke(this, new RegionCapturedEventArgs(result, elapsed));
        return result;
    }
}
