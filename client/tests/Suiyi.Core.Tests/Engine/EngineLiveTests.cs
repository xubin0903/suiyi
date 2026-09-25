using Suiyi.Core.Engine;
using Xunit.Abstractions;

namespace Suiyi.Core.Tests.Engine;

/// <summary>
/// 连接真实引擎。默认跳过；需要先启动
/// <c>python -m suiyi_engine serve --preload zh-en,en-zh</c> 并设置 <c>SUIYI_ENGINE_PORT</c>。
/// </summary>
[Trait("Category", "Engine")]
public sealed class EngineLiveTests(ITestOutputHelper output) : IDisposable
{
    private readonly EngineClient _client = new(EngineFactAttribute.Port ?? EngineClient.DefaultPort);

    public void Dispose() => _client.Dispose();

    [EngineFact]
    public async Task Health_ReportsPreloadedModels()
    {
        var health = await _client.GetHealthAsync();
        var languages = await _client.GetLanguagesAsync();

        output.WriteLine($"version={health.Version} loaded=[{string.Join(",", health.LoadedModels)}] pairs={languages.Pairs.Count}");
        Assert.Equal("ok", health.Status);
        Assert.Contains("opus-mt-zh-en", health.LoadedModels);
        Assert.Contains("opus-mt-en-zh", health.LoadedModels);
    }

    [EngineFact]
    public async Task Translate_ZhToEn()
    {
        var result = await _client.TranslateAsync("今天天气很好。", "zh", "en");

        output.WriteLine($"zh→en: {result.Text} route=[{string.Join(",", result.Route)}] server={result.ElapsedMs:F1}ms timeout={_client.GetTimeout("今天天气很好。", "zh", "en").TotalMilliseconds}ms");
        Assert.Equal(["opus-mt-zh-en"], result.Route);
        Assert.Matches("[A-Za-z]", result.Text);
    }

    [EngineFact]
    public async Task Translate_EnToZh()
    {
        var result = await _client.TranslateAsync("The weather is nice today.", "en", "zh");

        output.WriteLine($"en→zh: {result.Text} route=[{string.Join(",", result.Route)}] server={result.ElapsedMs:F1}ms");
        Assert.Equal(["opus-mt-en-zh"], result.Route);
        Assert.Contains(result.Text, c => c >= '\u4e00' && c <= '\u9fff');
    }

    [EngineFact]
    public async Task Service_ChinesePrimary_RetargetsChineseTextToEnglish()
    {
        using var service = new TranslationService(_client, () => ("zh", "en"));

        var outcome = await service.TranslateAsync("今天天气很好。");

        output.WriteLine($"service(zh,en) 今天天气很好。→ {outcome.Text} source={outcome.SourceLanguage} detected={outcome.SourceDetected} target={outcome.TargetLanguage} retargeted={outcome.Retargeted} requests={outcome.RequestCount} server={outcome.ServerElapsedMs:F1}ms client={outcome.ClientElapsed.TotalMilliseconds:F1}ms");
        Assert.True(outcome.Retargeted);
        Assert.Equal("en", outcome.TargetLanguage);
        Assert.Equal(2, outcome.RequestCount);
    }

    [EngineFact]
    public async Task Service_EnglishText_TranslatesToChinesePrimary()
    {
        using var service = new TranslationService(_client, () => ("zh", "en"));

        var outcome = await service.TranslateAsync("Good morning, everyone.");

        output.WriteLine($"service(zh,en) Good morning, everyone. → {outcome.Text} source={outcome.SourceLanguage} retargeted={outcome.Retargeted} server={outcome.ServerElapsedMs:F1}ms client={outcome.ClientElapsed.TotalMilliseconds:F1}ms");
        Assert.Equal("en", outcome.SourceLanguage);
        Assert.False(outcome.Retargeted);
    }
}
