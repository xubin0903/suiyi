using System.Text.Json;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

public class TimeoutPolicyTests
{
    private static readonly LanguagesResponse Languages =
        JsonSerializer.Deserialize<LanguagesResponse>(FakeEngineHandler.DefaultLanguages)!;

    private static readonly string[] AllLoaded =
        ["opus-mt-en-jap", "opus-mt-en-zh", "opus-mt-ja-en", "opus-mt-tc-big-zh-ja", "opus-mt-zh-en"];

    private static int Ms(string text, string source, string target, IReadOnlyCollection<string>? loaded, LanguagesResponse? languages = null) =>
        TimeoutPolicy.ComputeMilliseconds(text, source, target, languages ?? Languages, loaded);

    // ---- 规则 1：候选路线 ----

    [Fact]
    public void CandidateRoutes_ExplicitSource_IsThatPairOnly()
    {
        var routes = TimeoutPolicy.CandidateRoutes("en", "zh", Languages);

        var pair = Assert.Single(routes);
        Assert.Equal(("en", "zh"), (pair.Source, pair.Target));
    }

    [Fact]
    public void CandidateRoutes_Auto_IsZhEnJaMinusTarget()
    {
        var routes = TimeoutPolicy.CandidateRoutes("auto", "zh", Languages);

        Assert.Equal(["en", "ja"], routes.Select(r => r.Source).Order());
    }

    [Fact]
    public void CandidateRoutes_Auto_SkipsPairsMissingFromLanguages()
    {
        var languages = Languages with { Pairs = [.. Languages.Pairs.Where(p => !(p.Source == "ja" && p.Target == "en"))] };

        var routes = TimeoutPolicy.CandidateRoutes("auto", "en", languages);

        Assert.Equal("zh", Assert.Single(routes).Source);
    }

    [Fact]
    public void CandidateRoutes_NormalizesRegionAndCase()
    {
        var routes = TimeoutPolicy.CandidateRoutes("ZH-cn", "en_US", Languages);

        Assert.Equal(("zh", "en"), (Assert.Single(routes).Source, routes[0].Target));
    }

    [Fact]
    public void Compute_ExplicitSource_IgnoresUnloadedModelsOfOtherSources()
    {
        // en→zh 已加载，ja→zh（中转）的 ja-en 未加载：显式 en 不受影响。
        Assert.Equal(1500, Ms("Hello", "en", "zh", ["opus-mt-en-zh"]));
    }

    [Fact]
    public void Compute_Auto_AnyCandidateUnloaded_IsLazy()
    {
        // auto→zh 的候选包括 ja→zh（需要 opus-mt-ja-en），未加载即可能懒加载。
        Assert.Equal(10000, Ms("Hello", "auto", "zh", ["opus-mt-en-zh"]));
    }

    [Fact]
    public void Compute_Auto_UnavailableCandidateDoesNotForceLazy()
    {
        var languages = Languages with { Pairs = [.. Languages.Pairs.Where(p => !(p.Source == "ja" && p.Target == "en"))] };

        Assert.Equal(1500, Ms("你好", "auto", "en", ["opus-mt-zh-en"], languages));
    }

    // ---- 规则 2：懒加载 10000 ----

    [Fact]
    public void Compute_ModelNotLoaded_Is10000()
    {
        Assert.Equal(TimeoutPolicy.LazyLoadMs, Ms("你好", "zh", "en", []));
    }

    [Fact]
    public void Compute_LanguagesUnknown_IsLazy()
    {
        Assert.Equal(10000, TimeoutPolicy.ComputeMilliseconds("你好", "zh", "en", null, AllLoaded));
    }

    [Fact]
    public void Compute_LoadedModelsUnknown_IsLazy()
    {
        Assert.Equal(10000, Ms("你好", "zh", "en", null));
    }

    [Fact]
    public void Compute_LazyWithVeryLongText_NeverShorterThanParagraphRule()
    {
        Assert.Equal(15000, Ms(new string('a', 14000), "zh", "en", []));
    }

    // ---- 规则 3：短句直连 1500 ----

    [Fact]
    public void Compute_ShortDirectAllLoaded_Is1500()
    {
        Assert.Equal(TimeoutPolicy.ShortDirectMs, Ms("你好，世界", "zh", "en", AllLoaded));
    }

    [Fact]
    public void Compute_Exactly120Chars_IsShort()
    {
        Assert.Equal(1500, Ms(new string('a', 120), "en", "zh", AllLoaded));
    }

    [Fact]
    public void Compute_121Chars_IsParagraph()
    {
        Assert.Equal(3000, Ms(new string('a', 121), "en", "zh", AllLoaded));
    }

    [Theory]
    [InlineData("第一行\n第二行")]
    [InlineData("第一行\r第二行")]
    [InlineData("第一行\r\n第二行")]
    public void Compute_ShortTextWithNewline_IsParagraph(string text)
    {
        Assert.Equal(3000, Ms(text, "zh", "en", AllLoaded));
    }

    [Fact]
    public void Compute_ShortPivot_IsParagraph()
    {
        Assert.Equal(3000, Ms("こんにちは", "ja", "zh", AllLoaded));
    }

    [Fact]
    public void Compute_AutoWithPivotCandidate_IsParagraph()
    {
        // auto→zh 的候选含 ja→zh 中转。
        Assert.Equal(3000, Ms("Hello", "auto", "zh", AllLoaded));
    }

    [Fact]
    public void Compute_AutoAllDirect_Is1500()
    {
        // auto→en 的候选 zh→en、ja→en 都是直连。
        Assert.Equal(1500, Ms("你好", "auto", "en", AllLoaded));
    }

    [Fact]
    public void Compute_CountsCodePointsNotUtf16Units()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 120)); // 240 个 UTF-16 单元，120 个码位
        Assert.Equal(120, TimeoutPolicy.CountChars(emoji));
        Assert.Equal(1500, Ms(emoji, "en", "zh", AllLoaded));
    }

    // ---- 规则 4：段落 3000，超长加时，封顶 15000 ----

    [Fact]
    public void Compute_2000Chars_Is3000()
    {
        Assert.Equal(TimeoutPolicy.ParagraphMs, Ms(new string('a', 2000), "en", "zh", AllLoaded));
    }

    [Theory]
    [InlineData(2001, 3001)]
    [InlineData(2500, 3500)]
    [InlineData(5000, 6000)]
    [InlineData(14000, 15000)]
    [InlineData(50000, 15000)]
    public void Compute_LongText_AddsOneMsPerCharCapped(int length, int expected)
    {
        Assert.Equal(expected, Ms(new string('a', length), "en", "zh", AllLoaded));
    }

    [Fact]
    public void Compute_ReturnsTimeSpan()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(1500),
            TimeoutPolicy.Compute("hi", "en", "zh", Languages, AllLoaded));
    }
}
