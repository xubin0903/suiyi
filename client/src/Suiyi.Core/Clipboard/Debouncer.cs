namespace Suiyi.Core.Clipboard;

/// <summary>
/// 去抖：<see cref="Signal"/> 后等待 <see cref="Delay"/>，期间再次 <see cref="Signal"/> 则重新计时，
/// 静默满时长后调用一次回调。回调在计时器线程上执行。线程安全。
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly object _gate = new();
    private readonly Action _callback;
    private readonly ITimer _timer;
    private TimeSpan _delay;
    private bool _disposed;

    /// <summary>创建去抖器。</summary>
    public Debouncer(TimeSpan delay, Action callback, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _delay = delay;
        _timer = (timeProvider ?? TimeProvider.System)
            .CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>去抖时长。修改后从下一次 <see cref="Signal"/> 起生效。</summary>
    public TimeSpan Delay
    {
        get
        {
            lock (_gate)
            {
                return _delay;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            lock (_gate)
            {
                _delay = value;
            }
        }
    }

    /// <summary>记录一次事件并重新计时。</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _timer.Change(_delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>取消尚未触发的回调。</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _callback();
    }
}
