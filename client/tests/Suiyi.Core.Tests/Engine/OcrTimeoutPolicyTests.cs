using System.Text.Json;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

public class OcrTimeoutPolicyTests
{
    private static readonly LanguagesResponse Languages =
        JsonSerializer.Deserialize<LanguagesResponse>(FakeEngineHandler.DefaultLanguages)!;

    private static readonly string[] AllLoaded =
        ["opus-mt-en-jap", "opus-mt-en-zh", "opus-mt-ja-en", "opus-mt-tc-big-zh-ja", "opus-mt-zh-en"];

    private static int Ms(string source, string target, string? fallback, bool? ocrLoaded, IReadOnlyCollection<string>? loaded, LanguagesResponse? languages = null) =>
        TimeoutPolicy.ComputeOcrTranslateMilliseconds(source, target, fallback, ocrLoaded, languages ?? Languages, loaded);

    [Fact]
    public void Constants_AreFifteenAndThirtySeconds()
    {
        Assert.Equal(15000, TimeoutPolicy.OcrMs);
        Assert.Equal(30000, TimeoutPolicy.OcrColdMs);
    }

    [Fact]
    public void EverythingLoaded_IsOcrMs()
    {
        Assert.Equal(TimeoutPolicy.OcrMs, Ms("auto", "zh", "en", true, AllLoaded));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void OcrNotKnownLoaded_IsCold(bool? ocrLoaded)
    {
        Assert.Equal(TimeoutPolicy.OcrColdMs, Ms("auto", "zh", "en", ocrLoaded, AllLoaded));
    }

    [Fact]
    public void UnknownCaches_AreCold()
    {
        Assert.Equal(TimeoutPolicy.OcrColdMs, TimeoutPolicy.ComputeOcrTranslateMilliseconds("auto", "zh", "en", true, null, AllLoaded));
        Assert.Equal(TimeoutPolicy.OcrColdMs, Ms("auto", "zh", "en", true, null));
    }

    [Fact]
    public void CandidateModelMissing_IsCold()
    {
        // auto→zh 需要 ja→en→zh 的中转模型。
        Assert.Equal(TimeoutPolicy.OcrColdMs, Ms("auto", "zh", null, true, ["opus-mt-en-zh"]));
    }

    [Fact]
    public void FallbackRouteModelMissing_IsCold()
    {
        string[] loaded = ["opus-mt-en-zh", "opus-mt-ja-en"];
        Assert.Equal(TimeoutPolicy.OcrMs, Ms("auto", "zh", null, true, loaded));
        Assert.Equal(TimeoutPolicy.OcrColdMs, Ms("auto", "zh", "en", true, loaded)); // zh→en 未加载
    }

    [Fact]
    public void ExplicitSource_OnlyThatRouteAndFallbackCount()
    {
        Assert.Equal(TimeoutPolicy.OcrMs, Ms("en", "zh", null, true, ["opus-mt-en-zh"]));
    }

    [Fact]
    public void OcrOnly_DependsOnOcrLoaded()
    {
        Assert.Equal(TimeoutPolicy.OcrMs, TimeoutPolicy.ComputeOcrMilliseconds(true));
        Assert.Equal(TimeoutPolicy.OcrColdMs, TimeoutPolicy.ComputeOcrMilliseconds(false));
        Assert.Equal(TimeoutPolicy.OcrColdMs, TimeoutPolicy.ComputeOcrMilliseconds(null));
    }

    [Fact]
    public void Cold_IsNotShorterThanTranslateMax()
    {
        Assert.True(TimeoutPolicy.OcrColdMs >= TimeoutPolicy.OcrMs + TimeoutPolicy.LazyLoadMs);
        Assert.True(TimeoutPolicy.OcrMs >= TimeoutPolicy.MaxMs);
    }
}
