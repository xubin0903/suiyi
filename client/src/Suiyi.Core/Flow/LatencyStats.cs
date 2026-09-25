using System.Globalization;

namespace Suiyi.Core.Flow;

/// <summary>最近 N 次端到端延迟（毫秒）的滑动窗口，给出 P50 / P95（最近秩法）。线程安全。</summary>
public sealed class LatencyStats
{
    private readonly object _gate = new();
    private readonly Queue<double> _samples = new();

    /// <summary>创建统计。</summary>
    /// <param name="capacity">窗口大小，默认 20。</param>
    public LatencyStats(int capacity = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>窗口大小。</summary>
    public int Capacity { get; }

    /// <summary>当前样本数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>加入一个样本。</summary>
    public void Add(double milliseconds)
    {
        lock (_gate)
        {
            _samples.Enqueue(milliseconds);
            while (_samples.Count > Capacity)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>第 <paramref name="percent"/> 百分位（0–100，最近秩法）；没有样本时为 <see langword="null"/>。</summary>
    public double? Percentile(double percent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        double[] sorted;
        lock (_gate)
        {
            if (_samples.Count == 0)
            {
                return null;
            }

            sorted = [.. _samples.Order()];
        }

        var rank = Math.Max(1, (int)Math.Ceiling(percent / 100 * sorted.Length));
        return sorted[rank - 1];
    }

    /// <summary>例如「最近 20 次：P50 412 ms，P95 980 ms」。</summary>
    public string Summary()
    {
        var count = Count;
        if (count == 0)
        {
            return "暂无端到端延迟数据";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"最近 {count} 次：P50 {Percentile(50):0} ms，P95 {Percentile(95):0} ms");
    }
}
