using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Clipboard;

namespace Suiyi.Core.Tests.Clipboard;

public class ClipboardTextFilterTests
{
    private static readonly ClipboardFilterOptions Options = ClipboardFilterOptions.Default;

    private static RejectReason? ReasonOf(string? text, ClipboardFilterOptions? options = null) =>
        ClipboardTextFilter.Classify(text, options ?? Options).Reason;

    [Theory]
    [InlineData("今天天气很好")]
    [InlineData("Hello world")]
    [InlineData("ありがとうございます")]
    [InlineData("下午 3 点在公园门口见。")]
    [InlineData("I have 3 apples.")]
    [InlineData("OK")]
    [InlineData("Hi!")]
    [InlineData("访问 https://example.com 了解更多")]
    [InlineData("请联系 someone@example.com 获取帮助")]
    [InlineData("/ 是斜杠")]
    [InlineData("and/or")]
    [InlineData("deadbeef")]
    public void Classify_AcceptsTranslatableText(string text)
    {
        var result = ClipboardTextFilter.Classify(text, Options);

        Assert.True(result.IsAccepted, $"应接受，实际 {result.Reason}");
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void Normalize_TrimsAndUnifiesLineEndings()
    {
        Assert.Equal("第一行\n第二行\n第三行", ClipboardTextFilter.Normalize("  \r\n第一行\r\n第二行\r第三行 \t\r\n"));
        Assert.Equal(string.Empty, ClipboardTextFilter.Normalize(null));
    }

    [Fact]
    public void Classify_ReturnsNormalizedText()
    {
        var result = ClipboardTextFilter.Classify("  Hello\r\nworld  ", Options);

        Assert.True(result.IsAccepted);
        Assert.Equal("Hello\nworld", result.Text);
        Assert.Equal(11, result.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void Classify_RejectsBlank(string? text)
    {
        Assert.Equal(RejectReason.Empty, ReasonOf(text));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("好")]
    [InlineData("  x  ")]
    public void Classify_RejectsTooShort(string text)
    {
        Assert.Equal(RejectReason.TooShort, ReasonOf(text));
    }

    [Fact]
    public void Classify_RespectsMinCharsOption()
    {
        var options = Options with { MinChars = 5 };

        Assert.Equal(RejectReason.TooShort, ReasonOf("你好世界", options));
        Assert.Null(ReasonOf("你好，世界", options));
    }

    [Fact]
    public void Classify_MaxCharsBoundary()
    {
        Assert.Null(ReasonOf(new string('字', 2000)));

        var tooLong = ClipboardTextFilter.Classify(new string('字', 2001), Options);
        Assert.Equal(RejectReason.TooLong, tooLong.Reason);
        Assert.Equal(2001, tooLong.Length);
    }

    [Fact]
    public void Classify_MaxCharsCanBeRaisedForHotkey()
    {
        var options = Options with { MaxChars = 10000 };

        Assert.Null(ReasonOf(new string('字', 5000), options));
        Assert.Equal(RejectReason.TooLong, ReasonOf(new string('字', 10001), options));
    }

    [Fact]
    public void Classify_CountsCodePointsNotUtf16Units()
    {
        // 「a😀」是 2 个码位、3 个 UTF-16 单元。
        var options = Options with { MinChars = 2, MaxChars = 2 };

        var result = ClipboardTextFilter.Classify("a😀", options);

        Assert.True(result.IsAccepted);
        Assert.Equal(2, result.Length);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("¥1,234.50")]
    [InlineData("$ 99.9")]
    [InlineData("2026-09-25")]
    [InlineData("2026/09/25 12:30")]
    [InlineData("+86 138-0000-0000")]
    [InlineData("(010) 1234 5678")]
    [InlineData("50%")]
    [InlineData("１２３４５")]
    [InlineData("1234567890123456")]
    public void Classify_RejectsNumericLike(string text)
    {
        Assert.Equal(RejectReason.NumericLike, ReasonOf(text));
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("……")]
    [InlineData("😀😀")]
    [InlineData("-- --")]
    [InlineData("→ ←")]
    [InlineData("？！")]
    public void Classify_RejectsSymbolsOnly(string text)
    {
        Assert.Equal(RejectReason.SymbolsOnly, ReasonOf(text));
    }

    [Theory]
    [InlineData("https://example.com/a?b=1")]
    [InlineData("http://localhost:18780/health")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    [InlineData("ftp://files.example.org/pub")]
    [InlineData("www.example.com")]
    public void Classify_RejectsSingleUrl(string text)
    {
        Assert.Equal(RejectReason.Url, ReasonOf(text));
    }

    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("mailto:a.b@example.cn")]
    public void Classify_RejectsSingleEmail(string text)
    {
        Assert.Equal(RejectReason.Email, ReasonOf(text));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Suiyi\Suiyi.exe")]
    [InlineData("D:/data/file.txt")]
    [InlineData(@"\\server\share\doc.txt")]
    [InlineData("/usr/local/bin/python")]
    [InlineData("~/notes.md")]
    [InlineData("./run.sh")]
    [InlineData("../engine/pyproject.toml")]
    public void Classify_RejectsFilePath(string text)
    {
        Assert.Equal(RejectReason.FilePath, ReasonOf(text));
    }

    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}")]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void Classify_RejectsHexOrGuid(string text)
    {
        Assert.Equal(RejectReason.HexOrGuid, ReasonOf(text));
    }

    [Theory]
    [InlineData("int a = 1;\nint b = 2;\nreturn a + b;")]
    [InlineData("if (ready) {\n    Start();\n}")]
    [InlineData("function f() {\n  return 1;\n}\n// done")]
    public void Classify_RejectsCodeLike(string text)
    {
        Assert.Equal(RejectReason.CodeLike, ReasonOf(text));
    }

    [Theory]
    [InlineData("a = 1;\nb = 2;")]
    [InlineData("第一行；\n第二行；\n第三行；")]
    [InlineData("First line.\nSecond line;\nThird line.\nFourth line.")]
    [InlineData("今天天气很好。\n我们去公园吧。\n好的！")]
    public void Classify_DoesNotTreatProseAsCode(string text)
    {
        Assert.Null(ReasonOf(text));
    }

    [Fact]
    public void Classify_ValidatesOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardTextFilter.Classify("hello", Options with { MinChars = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardTextFilter.Classify("hello", Options with { MinChars = 10, MaxChars = 5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardTextFilter.Classify("hello", Options with { DuplicateWindow = TimeSpan.FromSeconds(-1) }));
    }

    [Fact]
    public void Evaluate_RejectsSameTextWithinWindow()
    {
        var clock = new FakeTimeProvider();
        var filter = new ClipboardTextFilter(clock);

        Assert.True(filter.Evaluate("Hello world", Options).IsAccepted);
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        var second = filter.Evaluate("  Hello world\r\n", Options);

        Assert.Equal(RejectReason.Duplicate, second.Reason);
        Assert.Equal(11, second.Length);
    }

    [Fact]
    public void Evaluate_AllowsSameTextAfterWindow()
    {
        var clock = new FakeTimeProvider();
        var filter = new ClipboardTextFilter(clock);

        Assert.True(filter.Evaluate("Hello world", Options).IsAccepted);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(filter.Evaluate("Hello world", Options).IsAccepted);
    }

    [Fact]
    public void Evaluate_DuplicateDoesNotExtendWindow()
    {
        var clock = new FakeTimeProvider();
        var filter = new ClipboardTextFilter(clock);

        filter.Evaluate("Hello world", Options);
        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(RejectReason.Duplicate, filter.Evaluate("Hello world", Options).Reason);
        clock.Advance(TimeSpan.FromSeconds(0.6));

        Assert.True(filter.Evaluate("Hello world", Options).IsAccepted);
    }

    [Fact]
    public void Evaluate_DifferentTextIsAccepted()
    {
        var filter = new ClipboardTextFilter(new FakeTimeProvider());

        Assert.True(filter.Evaluate("Hello world", Options).IsAccepted);
        Assert.True(filter.Evaluate("今天天气很好", Options).IsAccepted);
        Assert.True(filter.Evaluate("Hello world", Options).IsAccepted);
    }

    [Fact]
    public void Evaluate_ZeroWindowDisablesDedup()
    {
        var filter = new ClipboardTextFilter(new FakeTimeProvider());
        var options = Options with { DuplicateWindow = TimeSpan.Zero };

        Assert.True(filter.Evaluate("Hello world", options).IsAccepted);
        Assert.True(filter.Evaluate("Hello world", options).IsAccepted);
    }

    [Fact]
    public void Evaluate_RejectedTextIsNotRememberedForDedup()
    {
        var filter = new ClipboardTextFilter(new FakeTimeProvider());

        Assert.True(filter.Evaluate("Hello world", Options).IsAccepted);
        Assert.Equal(RejectReason.NumericLike, filter.Evaluate("12345", Options).Reason);

        Assert.Equal(RejectReason.Duplicate, filter.Evaluate("Hello world", Options).Reason);
    }

    [Fact]
    public void Reset_ClearsDedupState()
    {
        var filter = new ClipboardTextFilter(new FakeTimeProvider());
        filter.Evaluate("Hello world", Options);

        filter.Reset();

        Assert.True(filter.Evaluate("Hello world", Options).IsAccepted);
    }
}
