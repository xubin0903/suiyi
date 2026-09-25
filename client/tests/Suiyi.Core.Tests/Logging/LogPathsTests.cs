using Suiyi.Core.Logging;

namespace Suiyi.Core.Tests.Logging;

public class LogPathsTests
{
    private static readonly string LocalAppData = Path.Combine(Path.GetTempPath(), "LocalAppData");

    [Fact]
    public void ResolveDirectory_WithoutOverride_UsesLocalAppDataSuiyiLogs()
    {
        var dir = LogPaths.ResolveDirectory(null, LocalAppData);

        Assert.Equal(Path.GetFullPath(Path.Combine(LocalAppData, "suiyi", "logs")), dir);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDirectory_BlankOverride_IsIgnored(string overrideValue)
    {
        var dir = LogPaths.ResolveDirectory(overrideValue, LocalAppData);

        Assert.Equal(Path.GetFullPath(Path.Combine(LocalAppData, "suiyi", "logs")), dir);
    }

    [Fact]
    public void ResolveDirectory_WithOverride_UsesOverride()
    {
        var custom = Path.Combine(Path.GetTempPath(), "custom-logs");

        var dir = LogPaths.ResolveDirectory("  " + custom + "  ", LocalAppData);

        Assert.Equal(Path.GetFullPath(custom), dir);
    }

    [Fact]
    public void GetFileName_UsesPrefixAndCompactDate()
    {
        Assert.Equal("client-20260925.log", LogPaths.GetFileName("client", new DateOnly(2026, 9, 25)));
        Assert.Equal("engine-20260101.log", LogPaths.GetFileName("engine", new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void TryParseDate_RoundTripsFileName()
    {
        var date = new DateOnly(2026, 12, 31);

        var ok = LogPaths.TryParseDate(LogPaths.GetFileName("client", date), "client", out var parsed);

        Assert.True(ok);
        Assert.Equal(date, parsed);
    }

    [Theory]
    [InlineData("engine-20260925.log")]
    [InlineData("client-2026925.log")]
    [InlineData("client-20261332.log")]
    [InlineData("client-20260925.txt")]
    [InlineData("client.log")]
    public void TryParseDate_RejectsOtherFiles(string fileName)
    {
        Assert.False(LogPaths.TryParseDate(fileName, "client", out _));
    }
}
