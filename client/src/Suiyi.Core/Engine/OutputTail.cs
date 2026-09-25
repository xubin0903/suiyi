namespace Suiyi.Core.Engine;

/// <summary>线程安全地保留最后 N 行输出。</summary>
internal sealed class OutputTail
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly int _capacity;

    public OutputTail(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }
}
