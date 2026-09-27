using System.Diagnostics;
using System.Globalization;
using Suiyi.Core.Engine;
using Xunit.Abstractions;

namespace Suiyi.Core.Tests.Engine;

/// <summary>
/// #109 与真实引擎联调：OCR 子进程空闲退出后，预热把它拉起到 <c>ready</c>，随后的识别走热路径。默认跳过；需要一个 OCR 空闲退出时间很短的 #104 引擎：
/// <c>python -m suiyi_engine serve --port 18792 --ocr-idle-unload 8</c>，
/// <c>SUIYI_ENGINE_PORT=18792 dotnet test client/Suiyi.sln -c Release --filter FullyQualifiedName~EngineOcrPrewarmLiveTests</c>。
/// 引擎没有 <c>ocr_worker_state</c>（老引擎）、<c>ocr_idle_unload_s</c> 为 0 或超过 60 秒、或 OCR 不可用时只打印说明、不做断言。
/// </summary>
[Trait("Category", "Engine")]
public sealed class EngineOcrPrewarmLiveTests(ITestOutputHelper output) : IDisposable
{
    private const int MaxIdleSeconds = 60;

    private readonly ListLogger _log = new();
    private readonly EngineClient _client = new(EngineFactAttribute.Port ?? EngineClient.DefaultPort);

    public void Dispose() => _client.Dispose();

    [EngineFact]
    public async Task AfterWorkerExit_PrewarmBringsWorkerToReady()
    {
        var health = await _client.GetHealthAsync();
        if (health.OcrWorkerState is null || health.OcrIdleUnloadSeconds is not ({ } idle and > 0 and <= MaxIdleSeconds)
            || OcrPrewarmer.Decide(health) is OcrPrewarmDecision.SkipOcrUnavailable)
        {
            output.WriteLine($"ocr_worker_state={health.OcrWorkerState ?? "（无）"} ocr_idle_unload_s={health.OcrIdleUnloadSeconds?.ToString(CultureInfo.InvariantCulture) ?? "（无）"} ocr_error={health.OcrError?.Reason ?? "（无）"}，跳过断言");
            return;
        }

        // 等子进程空闲退出。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(idle + 15);
        while (health.OcrWorkerState != OcrWorkerStates.Stopped && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            health = await _client.GetHealthAsync();
        }

        Assert.Equal(OcrWorkerStates.Stopped, health.OcrWorkerState);
        Assert.Null(health.OcrWorkerPid);

        using var prewarmer = new OcrPrewarmer(_client, _log);
        var stopwatch = Stopwatch.StartNew();
        Assert.Equal(OcrPrewarmStart.Started, prewarmer.Prewarm());
        var returnedAfter = stopwatch.Elapsed;
        await prewarmer.LastRun.WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"Prewarm() 返回用时 {returnedAfter.TotalMilliseconds:F1} ms；后台预热完成 {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        foreach (var line in _log.Lines)
        {
            output.WriteLine("日志：" + line);
        }

        Assert.True(returnedAfter < TimeSpan.FromMilliseconds(100), "Prewarm() 必须立即返回");
        Assert.Equal(1, prewarmer.RequestsSent);
        var warm = await _client.GetHealthAsync();
        Assert.Equal(OcrWorkerStates.Ready, warm.OcrWorkerState);
        Assert.NotNull(warm.OcrWorkerPid);
        Assert.Equal(OcrPrewarmDecision.SkipAlreadyWarm, OcrPrewarmer.Decide(warm));

        var hot = Stopwatch.StartNew();
        var result = await _client.OcrAsync(OcrPrewarmer.BlankPng);
        output.WriteLine($"预热后白图 /ocr：server={result.ElapsedMs:F1}ms client={hot.Elapsed.TotalMilliseconds:F0}ms");
        Assert.Empty(result.Paragraphs);
    }

    /// <summary>
    /// 对比：子进程退出后，无预热 vs 预热并等待 2 秒（模拟拖选区）的首次 <c>/ocr_translate</c> 耗时。只打印数字、不断言耗时（受机器负载影响）。
    /// 需要额外设置 <c>SUIYI_ENGINE_OCR_PNG</c>（中文 PNG），并加载 zh→en。
    /// </summary>
    [EngineOcrFact]
    public async Task ColdFirstOcrTranslate_WithAndWithoutPrewarm_PrintsTimings()
    {
        var health = await _client.GetHealthAsync();
        if (health.OcrWorkerState is null || health.OcrIdleUnloadSeconds is not ({ } idle and > 0 and <= MaxIdleSeconds))
        {
            output.WriteLine("需要 #104 引擎且 ocr_idle_unload_s 在 1–60 秒，跳过");
            return;
        }

        var png = await File.ReadAllBytesAsync(EngineOcrFactAttribute.PngPath!);
        await _client.OcrTranslateAsync(png, "zh", "en", null); // 让翻译模型先热起来，只比较 OCR 冷启动
        foreach (var prewarm in new[] { false, true, false, true })
        {
            await WaitForWorkerStoppedAsync(idle);
            using var prewarmer = new OcrPrewarmer(_client, _log);
            if (prewarm)
            {
                prewarmer.Prewarm();
                await Task.Delay(TimeSpan.FromSeconds(2)); // 用户拖选区
            }

            var stopwatch = Stopwatch.StartNew();
            var result = await _client.OcrTranslateAsync(png, "zh", "en", null);
            output.WriteLine($"{(prewarm ? "预热 + 2 s" : "无预热")}：首次 /ocr_translate client={stopwatch.Elapsed.TotalMilliseconds:F0}ms server_ocr={result.ElapsedMs?.Ocr:F0}ms");
            Assert.NotEmpty(result.Paragraphs);
            await prewarmer.LastRun.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    private async Task WaitForWorkerStoppedAsync(double idle)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(idle + 15);
        while ((await _client.GetHealthAsync()).OcrWorkerState != OcrWorkerStates.Stopped)
        {
            Assert.True(DateTime.UtcNow < deadline, "OCR 子进程没有按时空闲退出");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}
