using Suiyi.Core.Engine;
using Xunit.Abstractions;

namespace Suiyi.Core.Tests.Engine;

/// <summary>
/// #94 与真实引擎联调：空闲卸载后再翻译按冷启动超时且成功。默认跳过；需要先启动一个空闲卸载时间很短的引擎并设置 <c>SUIYI_ENGINE_PORT</c>：
/// <c>python -m suiyi_engine serve --port 18791 --preload zh-en --model-idle-unload 8</c>，
/// <c>SUIYI_ENGINE_PORT=18791 dotnet test client/Suiyi.sln -c Release --filter FullyQualifiedName~EngineColdStartLiveTests</c>。
/// 引擎的 <c>model_idle_unload_s</c> 为 0、缺失或超过 60 秒时只打印说明、不做断言（等太久）。
/// </summary>
[Trait("Category", "Engine")]
public sealed class EngineColdStartLiveTests(ITestOutputHelper output) : IDisposable
{
    private const int MaxIdleSeconds = 60;

    private readonly ListLogger _log = new();
    private readonly EngineClient _client = new(EngineFactAttribute.Port ?? EngineClient.DefaultPort);

    public void Dispose() => _client.Dispose();

    [EngineFact]
    public async Task AfterIdleUnload_TranslateUsesColdTimeoutAndSucceeds()
    {
        _client.Logger = _log;
        var health = await _client.GetHealthAsync();
        if (health.ModelIdleUnloadSeconds is not ({ } idle and > 0 and <= MaxIdleSeconds))
        {
            output.WriteLine($"model_idle_unload_s={health.ModelIdleUnloadSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "（无）"}，需要 1–{MaxIdleSeconds} 秒的引擎，跳过断言");
            return;
        }

        var first = await _client.TranslateAsync("今天天气很好。", "zh", "en");
        Assert.Equal(["opus-mt-zh-en"], first.Route);

        await Task.Delay(TimeSpan.FromSeconds(idle + 3));

        // 用另一个客户端看服务端已卸载；被测客户端不刷新 /health，模拟看门狗还没来得及刷新（缓存仍以为已加载）的情况。
        using (var probe = new EngineClient(EngineFactAttribute.Port ?? EngineClient.DefaultPort))
        {
            var unloaded = await probe.GetHealthAsync();
            output.WriteLine($"空闲 {idle + 3}s 后 loaded_models=[{string.Join(",", unloaded.LoadedModels)}]");
            Assert.DoesNotContain("opus-mt-zh-en", unloaded.LoadedModels);
        }

        Assert.Contains("opus-mt-zh-en", _client.KnownLoadedModels!);
        Assert.Equal(TimeSpan.FromMilliseconds(TimeoutPolicy.LazyLoadMs), _client.GetTimeout("今天天气很好。", "zh", "en"));

        var started = DateTime.UtcNow;
        var again = await _client.TranslateAsync("今天天气很好。", "zh", "en");
        output.WriteLine($"卸载后再次 zh→en：server={again.ElapsedMs:F1}ms client={(DateTime.UtcNow - started).TotalMilliseconds:F0}ms");
        foreach (var line in _log.Lines)
        {
            output.WriteLine("日志：" + line);
        }

        Assert.Equal(first.Text, again.Text);
        Assert.Contains(_log.Lines, line => line.Contains("按冷启动超时 10000 ms", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, line => line.Contains("自动重试", StringComparison.Ordinal));
    }
}
