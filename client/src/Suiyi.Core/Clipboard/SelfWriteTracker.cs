namespace Suiyi.Core.Clipboard;

/// <summary>
/// 记录「本程序自己写入剪贴板」产生的序号，以及一次性的「忽略下一次变化」窗口，
/// 让监听器不把自己的写入当成用户复制。线程安全。
/// </summary>
public sealed class SelfWriteTracker
{
    private const int MaxRemembered = 16;

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Queue<uint> _ownSequences = new();
    private DateTimeOffset? _suppressUntil;

    /// <summary>创建跟踪器。</summary>
    /// <param name="timeProvider">时钟，测试时注入；默认 <see cref="TimeProvider.System"/>。</param>
    public SelfWriteTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>记录一次自身写入后的剪贴板序号。</summary>
    public void RecordOwnWrite(uint sequenceNumber)
    {
        lock (_gate)
        {
            if (_ownSequences.Contains(sequenceNumber))
            {
                return;
            }

            _ownSequences.Enqueue(sequenceNumber);
            while (_ownSequences.Count > MaxRemembered)
            {
                _ownSequences.Dequeue();
            }
        }
    }

    /// <summary>
    /// 在 <paramref name="window"/> 内忽略下一次剪贴板变化（只忽略一次）。
    /// 供快捷键模拟 Ctrl+C 时使用（#33）。
    /// </summary>
    public void SuppressNext(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        lock (_gate)
        {
            _suppressUntil = _timeProvider.GetUtcNow() + window;
        }
    }

    /// <summary>取消尚未消耗的 <see cref="SuppressNext"/>。</summary>
    public void CancelSuppress()
    {
        lock (_gate)
        {
            _suppressUntil = null;
        }
    }

    /// <summary>
    /// 该序号的变化是否应忽略：是自身写入，或处在 <see cref="SuppressNext"/> 窗口内（会消耗该窗口）。
    /// </summary>
    public bool ShouldIgnore(uint sequenceNumber)
    {
        lock (_gate)
        {
            if (_ownSequences.Contains(sequenceNumber))
            {
                // 自身写入同时消耗未用的 SuppressNext（复制译文时两者同时设置），避免它吞掉用户下一次复制。
                _suppressUntil = null;
                return true;
            }

            if (_suppressUntil is { } until)
            {
                _suppressUntil = null;
                if (_timeProvider.GetUtcNow() <= until)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
