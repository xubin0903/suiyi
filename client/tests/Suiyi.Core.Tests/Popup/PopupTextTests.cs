using Suiyi.Core.Popup;

namespace Suiyi.Core.Tests.Popup;

public sealed class PopupTextTests
{
    [Theory]
    [InlineData("zh", "en", false, "中文 → English")]
    [InlineData("en", "zh", true, "English（自动） → 中文")]
    [InlineData("ja", "zh", false, "日本語 → 中文")]
    [InlineData(null, "ja", true, "自动 → 日本語")]
    [InlineData("auto", "en", false, "自动 → English")]
    [InlineData("fr", "zh", false, "fr → 中文")]
    public void LanguageLabel(string? source, string target, bool detected, string expected)
    {
        Assert.Equal(expected, PopupText.LanguageLabel(source, target, detected));
    }

    [Fact]
    public void LanguageLabel_EmptyTarget_Throws()
    {
        Assert.Throws<ArgumentException>(() => PopupText.LanguageLabel("zh", " ", false));
    }

    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(320, "320 ms")]
    [InlineData(999.4, "999 ms")]
    [InlineData(1000, "1.0 s")]
    [InlineData(1450, "1.5 s")]
    [InlineData(12345, "12.3 s")]
    public void FormatElapsed(double ms, string expected)
    {
        Assert.Equal(expected, PopupText.FormatElapsed(TimeSpan.FromMilliseconds(ms)));
    }

    [Fact]
    public void SourcePreview_CollapsesWhitespace()
    {
        Assert.Equal("a b c", PopupText.SourcePreview("  a \r\n\t b   c \n"));
    }

    [Fact]
    public void SourcePreview_TruncatesWithEllipsis()
    {
        var preview = PopupText.SourcePreview(new string('字', 200));

        Assert.Equal(PopupText.SourcePreviewLength, preview.Length);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void SourcePreview_ExactLength_NotTruncated()
    {
        var text = new string('a', 10);

        Assert.Equal(text, PopupText.SourcePreview(text, 10));
        Assert.Equal("aaaaaaaaa…", PopupText.SourcePreview(text + "b", 10));
    }

    [Fact]
    public void SourcePreview_Empty()
    {
        Assert.Equal(string.Empty, PopupText.SourcePreview(" \n "));
    }

    [Theory]
    [InlineData("zh", "Microsoft YaHei UI")]
    [InlineData(null, "Microsoft YaHei UI")]
    [InlineData("JA", "Yu Gothic UI")]
    [InlineData("en", "Segoe UI")]
    public void FontFamily_PrimaryByLanguage(string? language, string primary)
    {
        var family = PopupText.FontFamilyFor(language);

        Assert.StartsWith(primary + ",", family, StringComparison.Ordinal);
        Assert.Contains("Microsoft YaHei UI", family, StringComparison.Ordinal);
        Assert.Contains("Yu Gothic UI", family, StringComparison.Ordinal);
        Assert.Contains("Segoe UI", family, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceChoices_AreZhEnJa()
    {
        Assert.Equal(["zh", "en", "ja"], PopupText.SourceChoices.Select(l => l.Code));
    }
}
