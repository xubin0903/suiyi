using Suiyi.Core.Engine;
using Suiyi.Core.Flow;
using Xunit.Abstractions;

namespace Suiyi.Core.Tests.Engine;

/// <summary>
/// 连接真实引擎的 OCR 联调（#56 验收项，需 #53 服务端实现合入）。默认跳过；启动
/// <c>python -m suiyi_engine serve --preload zh-en,en-zh</c> 后设置 <c>SUIYI_ENGINE_PORT</c> 与
/// <c>SUIYI_ENGINE_OCR_PNG</c>（一张含中文的 PNG 截图路径），再运行
/// <c>dotnet test client/Suiyi.sln --filter Category=Engine</c>。
/// </summary>
[Trait("Category", "Engine")]
public sealed class EngineOcrLiveTests(ITestOutputHelper output) : IDisposable
{
    private readonly EngineClient _client = new(EngineFactAttribute.Port ?? EngineClient.DefaultPort);

    public void Dispose() => _client.Dispose();

    [EngineOcrFact]
    public async Task OcrTranslate_ChineseScreenshot_ToEnglish()
    {
        var png = await File.ReadAllBytesAsync(EngineOcrFactAttribute.PngPath!);
        using var service = new OcrTranslationService(_client, () => ("en", "zh"));

        var outcome = await service.TranslateImageAsync(png);
        var popup = OcrResultMapper.Map(outcome.Response, outcome.Target);

        var elapsed = outcome.Response.ElapsedMs;
        output.WriteLine($"size={outcome.ImageWidth}x{outcome.ImageHeight} bytes={outcome.ByteCount} paragraphs={outcome.Response.Paragraphs.Count} ocr={elapsed?.Ocr:F0}ms translate={elapsed?.Translate:F0}ms total={elapsed?.Total:F0}ms client={outcome.ClientElapsed.TotalMilliseconds:F0}ms ocr_loaded={_client.KnownOcrLoaded}");
        output.WriteLine($"source={popup.Source} target={popup.Target} → {popup.TranslationText}");
        Assert.False(popup.IsEmpty);
        Assert.Matches("[A-Za-z]", popup.TranslationText);
    }
}

/// <summary>需要 <c>SUIYI_ENGINE_PORT</c> 与 <c>SUIYI_ENGINE_OCR_PNG</c>（存在的文件），否则跳过。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EngineOcrFactAttribute : FactAttribute
{
    public const string PngVariable = "SUIYI_ENGINE_OCR_PNG";

    public EngineOcrFactAttribute()
    {
        if (EngineFactAttribute.Port is null || PngPath is null)
        {
            Skip = $"需要支持 OCR 的运行中引擎：设置 {EngineFactAttribute.PortVariable} 与 {PngVariable}（中文 PNG 路径）后运行";
        }
    }

    public static string? PngPath =>
        Environment.GetEnvironmentVariable(PngVariable) is { Length: > 0 } path && File.Exists(path) ? path : null;
}
