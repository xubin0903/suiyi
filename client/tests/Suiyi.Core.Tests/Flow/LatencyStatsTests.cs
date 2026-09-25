using Suiyi.Core.Flow;

namespace Suiyi.Core.Tests.Flow;

public sealed class LatencyStatsTests
{
    [Fact]
    public void Empty_HasNoPercentiles()
    {
        var stats = new LatencyStats();

        Assert.Equal(0, stats.Count);
        Assert.Null(stats.Percentile(95));
        Assert.Equal("暂无端到端延迟数据", stats.Summary());
    }

    [Fact]
    public void Percentile_UsesNearestRank()
    {
        var stats = new LatencyStats();
        foreach (var ms in Enumerable.Range(1, 20).Reverse())
        {
            stats.Add(ms * 100);
        }

        Assert.Equal(1000, stats.Percentile(50));
        Assert.Equal(1900, stats.Percentile(95));
        Assert.Equal(2000, stats.Percentile(100));
        Assert.Equal(100, stats.Percentile(0));
        Assert.Equal("最近 20 次：P50 1000 ms，P95 1900 ms", stats.Summary());
    }

    [Fact]
    public void Add_KeepsOnlyLatestCapacitySamples()
    {
        var stats = new LatencyStats(3);
        stats.Add(5000);
        stats.Add(100);
        stats.Add(200);
        stats.Add(300);

        Assert.Equal(3, stats.Count);
        Assert.Equal(300, stats.Percentile(100));
    }

    [Fact]
    public void SingleSample()
    {
        var stats = new LatencyStats();
        stats.Add(123.4);

        Assert.Equal("最近 1 次：P50 123 ms，P95 123 ms", stats.Summary());
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyStats(0));
        var stats = new LatencyStats();
        Assert.Throws<ArgumentOutOfRangeException>(() => stats.Percentile(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stats.Percentile(101));
    }
}
