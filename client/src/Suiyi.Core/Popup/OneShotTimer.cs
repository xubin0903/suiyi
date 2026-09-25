namespace Suiyi.Core.Popup;

/// <summary>
/// 可重启的一次性计时器。回调经 <c>dispatch</c> 切回 UI 线程；重启或停止后，已排队的旧回调会被丢弃。
/// 只能在 UI 线程上调用。
/// </summary>
internal sealed class OneShotTimer(TimeProvider timeProvider, Action<Action> dispatch) : IDisposable
{
    private ITimer? _timer;
    private int _generation;

    public bool IsRunning => _timer is not null;

    public void Start(TimeSpan due, Action callback)
    {
        Stop();
        var generation = _generation;
        _timer = timeProvider.CreateTimer(
            _ => dispatch(() =>
            {
                if (generation != _generation)
                {
                    return;
                }

                Stop();
                callback();
            }),
            null,
            due,
            Timeout.InfiniteTimeSpan);
    }

    public void Stop()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();
}
