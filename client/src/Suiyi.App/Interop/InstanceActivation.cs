namespace Suiyi.App.Interop;

/// <summary>
/// 第二个实例通知第一个实例「有人又启动了随译」：命名事件 <c>Local\Suiyi.Client.Activate</c>。
/// 第一个实例收到后在托盘显示「随译已在运行」。
/// </summary>
internal sealed class InstanceActivation : IDisposable
{
    private const string EventName = @"Local\Suiyi.Client.Activate";

    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _registration;

    /// <summary>作为第一个实例开始等待通知。<paramref name="onActivated"/> 在线程池线程上调用。</summary>
    public InstanceActivation(Action onActivated)
    {
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _event, (_, _) => onActivated(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>作为第二个实例通知已在运行的实例；对方不存在或尚未就绪时返回 <see langword="false"/>。</summary>
    public static bool SignalExisting()
    {
        if (!EventWaitHandle.TryOpenExisting(EventName, out var handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _event.Dispose();
    }
}
