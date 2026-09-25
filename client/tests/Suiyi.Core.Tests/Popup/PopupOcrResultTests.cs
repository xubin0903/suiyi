using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Popup;

public sealed class PopupOcrResultTests
{
    [Fact]
    public void Text_JoinsParagraphsWithBlankLine_SkipsBlank()
    {
        var result = new PopupOcrResult([" 第一段 ", "", "第二段"], ["First.", " ", "Second."], "zh", "en");

        Assert.Equal("第一段\n\n第二段", result.SourceText);
        Assert.Equal("First.\n\nSecond.", result.TranslationText);
        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void Empty_Factory()
    {
        var empty = PopupOcrResult.Empty("ja");

        Assert.True(empty.IsEmpty);
        Assert.Equal("ja", empty.Target);
        Assert.Null(empty.Source);
        Assert.Equal(string.Empty, empty.SourceText);
    }
}
