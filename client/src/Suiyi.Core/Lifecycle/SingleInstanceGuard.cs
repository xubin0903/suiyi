namespace Suiyi.Core.Lifecycle;

/// <summary>
/// 基于命名 Mutex 的单实例判定。必须在同一线程上获取与释放（WPF 中即 UI 线程）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>客户端默认的 Mutex 名（当前会话内唯一）。</summary>
    public const string DefaultName = @"Local\Suiyi.Client";

    private readonly Mutex _mutex;
    private bool _released;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// 尝试成为唯一实例。成功时返回持有 Mutex 的守卫；已有实例运行时返回 <see langword="false"/>。
    /// 上一个实例异常退出遗留的 Mutex（<see cref="AbandonedMutexException"/>）视为获取成功。
    /// </summary>
    public static bool TryAcquire(string name, out SingleInstanceGuard? guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var mutex = new Mutex(initiallyOwned: false, name);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        return true;
    }

    /// <summary>释放 Mutex。</summary>
    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
