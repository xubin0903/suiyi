using Suiyi.Core.Glossary;

namespace Suiyi.Core.Tests.Glossary;

public sealed class GlossaryStatusTests
{
    private static GlossaryStatus Status(string? error = null, params string[] warnings) =>
        new(true, 300, 2, "/x/glossary.tsv", error, warnings);

    [Fact]
    public void Equality_ComparesWarningsByValue()
    {
        Assert.Equal(Status(null, "a", "b"), Status(null, "a", "b"));
        Assert.Equal(Status(null, "a").GetHashCode(), Status(null, "a").GetHashCode());
        Assert.NotEqual(Status(null, "a"), Status(null, "b"));
        Assert.NotEqual(Status(null, "a"), Status(null, "a", "b"));
        Assert.NotEqual(Status("e"), Status());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("文件过大", true)]
    public void HasError(string? error, bool expected) => Assert.Equal(expected, Status(error).HasError);

    [Fact]
    public void MenuLines_Unknown_And_NotSupported()
    {
        Assert.Equal([GlossaryStatusText.Unknown], GlossaryStatusText.MenuLines(null, null));
        Assert.Equal([GlossaryStatusText.Unknown], GlossaryStatusText.MenuLines(null, true));
        Assert.Equal([GlossaryStatusText.NotSupported], GlossaryStatusText.MenuLines(null, false));
    }

    [Fact]
    public void MenuLines_Counts()
    {
        Assert.Equal(["内置 300 条 · 我的 2 条"], GlossaryStatusText.MenuLines(Status(), true));
    }

    [Fact]
    public void MenuLines_ErrorAndWarnings_Truncated()
    {
        var lines = GlossaryStatusText.MenuLines(Status("文件\n过大", new string('x', 100), "b"), true);

        Assert.Equal(4, lines.Count);
        Assert.Equal("我的术语表未生效：文件 过大", lines[1]);
        Assert.Equal("有 2 行被跳过，例如：", lines[2]);
        Assert.Equal(GlossaryStatusText.MaxLineLength, lines[3].Length);
        Assert.EndsWith("…", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void About_ListsEverything()
    {
        Assert.Equal("专业术语保护：关\n" + GlossaryStatusText.NotSupported, GlossaryStatusText.About(false, null, false));
        Assert.Equal(
            "专业术语保护：开（内置 300 条，我的 2 条）\n我的术语表未生效：坏了\n· w1",
            GlossaryStatusText.About(true, Status("坏了", "w1"), true));
    }

    [Fact]
    public void Reloaded_Variants()
    {
        Assert.Equal("术语表已重新加载：我的 2 条，内置 300 条", GlossaryStatusText.Reloaded(Status()));
        Assert.Equal("术语表已重新加载：我的 2 条，内置 300 条；1 行被跳过：第 3 行：缺少目标词", GlossaryStatusText.Reloaded(Status(null, "第 3 行：缺少目标词")));
        Assert.Equal("我的术语表未生效：文件过大（已改用内置术语表）", GlossaryStatusText.Reloaded(Status("文件过大")));
    }
}
