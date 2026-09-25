using System.Globalization;
using Suiyi.Core.Capture;
using Suiyi.Core.Engine;
using Suiyi.Core.Logging;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Flow;

/// <summary>框选翻译的触发来源。</summary>
public enum RegionTranslateTrigger
{
    /// <summary>框选快捷键（<c>hotkey.region</c>）。</summary>
    Hotkey,

    /// <summary>托盘「框选翻译」。</summary>
    Tray,
}

/// <summary>
/// 框选翻译（#58）：框选 → <see cref="IOcrTranslationService"/> → <see cref="OcrResultMapper"/> → 浮窗（围绕选区）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>开始框选即取消进行中的请求（文本或框选）并隐藏浮窗；框选取消或截屏失败不弹浮窗（失败时托盘提示一次）。</item>
/// <item>遮罩显示期间重复触发、文本捕获（剪贴板 / 快捷键 / 托盘）一律忽略，避免浮窗盖在遮罩上。</item>
/// <item>「暂停监听」不影响框选翻译。</item>
/// <item>服务未就绪、连接被拒、重试时重启服务、30 秒等待上限：与复制翻译共用同一套规则。</item>
/// <item>截图只在内存中：识别成功、浮窗关闭或新请求开始后即丢弃引用；出错时保留到浮窗关闭，供「重试」复用同一张 PNG。</item>
/// <item>端到端延迟 <c>ocr_e2e_ms</c>：从框选完成（鼠标松开、拿到 PNG）到浮窗结果渲染完成，单独统计 <see cref="OcrLatency"/>。</item>
/// </list>
/// </remarks>
public sealed partial class TranslateFlowCoordinator
{
    private readonly IOcrTranslationService? _ocr;
    private readonly RegionCaptureTrigger? _region;
    private OcrRequest? _lastOcr;

    /// <summary>一次框选翻译完成并显示结果（端到端计时之后）。</summary>
    public event EventHandler<RegionTranslateCompletedEventArgs>? RegionCompleted;

    /// <summary>框选翻译最近若干次 <c>ocr_e2e_ms</c>（热路径；等待服务就绪的请求不计入）。</summary>
    public LatencyStats OcrLatency { get; }

    /// <summary>是否启用了框选翻译（构造时传入了 OCR 服务与框选入口）。</summary>
    public bool IsRegionTranslateEnabled => _ocr is not null && _region is not null;

    /// <summary>是否保留着可供重试的截图（测试与诊断用）。</summary>
    public bool HasRetainedScreenshot => _lastOcr is not null;

    private bool IsRegionCapturing => _region?.IsCapturing == true;

    /// <summary>
    /// 开始一次框选翻译（快捷键或托盘）。遮罩显示期间重复调用被忽略。完成（或取消）时返回；识别与翻译在后台继续。
    /// </summary>
    /// <param name="trigger">来源。</param>
    /// <param name="cancellationToken">外部取消（退出程序）：关闭遮罩，不弹浮窗。</param>
    public async Task TranslateRegionAsync(RegionTranslateTrigger trigger = RegionTranslateTrigger.Hotkey, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        if (_ocr is null || _region is null)
        {
            _logger.Info("框选翻译：未启用（翻译服务未启动）");
            return;
        }

        if (_region.IsCapturing)
        {
            _logger.Info($"框选翻译：遮罩已显示，忽略重复触发（{trigger}）");
            return;
        }

        // 隐藏当前浮窗、取消进行中的请求：新的框选优先。
        CancelInFlight();
        _lastOcr = null;
        _popup.Close(PopupCloseReason.Program);

        var capture = await _region.RunAsync(cancellationToken).ConfigureAwait(true);
        if (capture is null || _disposed || cancellationToken.IsCancellationRequested)
        {
            return; // 取消或失败：不弹浮窗。
        }

        var released = _timeProvider.GetTimestamp();
        var bounds = capture.Bounds;
        Start(new OcrRequest(
            capture.Png,
            new PopupRect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            trigger,
            bounds.Width,
            bounds.Height,
            released));
    }

    private async Task RunOcrAsync(OcrRequest request, bool waitedForEngine)
    {
        var generation = ++_generation;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _popup.ShowOcrLoading(request.Anchor);

        OcrTranslationOutcome outcome;
        try
        {
            outcome = await _ocr!.TranslateImageAsync(request.Png, cts.Token).ConfigureAwait(false);
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
#pragma warning disable CA1031 // 识别失败不能让异常逃逸到消息循环。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _dispatch(() => OnFailed(generation, request, new EngineException(EngineErrorKind.Unknown, ex.GetType().Name, ex)));
            return;
        }
        finally
        {
            cts.Dispose();
        }

        _dispatch(() => OnOcrSucceeded(generation, request, outcome, waitedForEngine));
    }

    private void OnOcrSucceeded(int generation, OcrRequest request, OcrTranslationOutcome outcome, bool waitedForEngine)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        _cts = null;
        _lastOcr = null; // 识别成功：截图用完即丢。
        var result = OcrResultMapper.Map(outcome.Response, outcome.Target) with { Elapsed = outcome.ClientElapsed };
        _popup.ShowOcrResult(result, request.Anchor);

        _afterRender(() =>
        {
            var e2e = _timeProvider.GetElapsedTime(request.Started);
            if (!waitedForEngine)
            {
                OcrLatency.Add(e2e.TotalMilliseconds);
            }

            var elapsed = outcome.Response.ElapsedMs;
            _logger.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"框选翻译完成：trigger={request.Trigger} size={request.Width}x{request.Height} bytes={request.Png.Length}"
                + $" paragraphs={result.SourceParagraphs.Count} empty={result.IsEmpty} untranslated={result.UntranslatedParagraphs.Count}"
                + $" {result.Source ?? "?"}→{result.Target}{(outcome.Retargeted ? "（改译）" : string.Empty)}"
                + $" ocr_e2e_ms={e2e.TotalMilliseconds:0} http_ms={outcome.ClientElapsed.TotalMilliseconds:0}"
                + $" server_ocr_ms={elapsed?.Ocr ?? 0:0} server_translate_ms={elapsed?.Translate ?? 0:0} server_total_ms={elapsed?.Total ?? 0:0}"
                + $"{(waitedForEngine ? "（含等待服务就绪，不计入统计）" : string.Empty)}；框选{OcrLatency.Summary()}"));
            RegionCompleted?.Invoke(this, new RegionTranslateCompletedEventArgs(request.Trigger, outcome, result, e2e, waitedForEngine));
        });
    }

    private void OnRegionCaptureFailed(object? sender, RegionCaptureFailedEventArgs e) =>
        _tray.ShowNotification(AppTitle, "框选截屏失败，请重试（详情见日志）");

    /// <summary>框选翻译请求。<paramref name="Png"/> 只在内存中，不落盘。</summary>
    private sealed record OcrRequest(
        ReadOnlyMemory<byte> Png,
        PopupRect Anchor,
        RegionTranslateTrigger Trigger,
        int Width,
        int Height,
        long Started,
        long? WaitSince = null)
        : FlowRequest(Started, WaitSince);
}

/// <summary><see cref="TranslateFlowCoordinator.RegionCompleted"/> 参数。</summary>
/// <param name="trigger">来源。</param>
/// <param name="outcome">服务结果。</param>
/// <param name="result">浮窗显示的内容。</param>
/// <param name="endToEnd">从框选完成到浮窗渲染完成（<c>ocr_e2e_ms</c>）。</param>
/// <param name="waitedForEngine">是否等待过服务就绪（不计入延迟统计）。</param>
public sealed class RegionTranslateCompletedEventArgs(
    RegionTranslateTrigger trigger,
    OcrTranslationOutcome outcome,
    PopupOcrResult result,
    TimeSpan endToEnd,
    bool waitedForEngine) : EventArgs
{
    /// <summary>来源。</summary>
    public RegionTranslateTrigger Trigger { get; } = trigger;

    /// <summary>服务结果。</summary>
    public OcrTranslationOutcome Outcome { get; } = outcome;

    /// <summary>浮窗显示的内容。</summary>
    public PopupOcrResult Result { get; } = result;

    /// <summary>端到端耗时。</summary>
    public TimeSpan EndToEnd { get; } = endToEnd;

    /// <summary>是否等待过服务就绪。</summary>
    public bool WaitedForEngine { get; } = waitedForEngine;
}
