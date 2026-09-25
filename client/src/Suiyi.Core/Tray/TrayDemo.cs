namespace Suiyi.Core.Tray;

/// <summary><c>--tray-demo</c> 的状态轮换序列：每一步是一组 <see cref="ITrayService"/> 调用。</summary>
public static class TrayDemo
{
    /// <summary>轮换间隔。</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(2);

    /// <summary>四步：正在准备 → 就绪 → 已暂停 → 异常，循环。</summary>
    public static void ApplyStep(ITrayService tray, int step)
    {
        ArgumentNullException.ThrowIfNull(tray);
        switch (((step % 4) + 4) % 4)
        {
            case 0:
                tray.SetPaused(false);
                tray.SetStatus(TrayStatus.Preparing);
                break;
            case 1:
                tray.SetStatus(TrayStatus.Ready);
                break;
            case 2:
                tray.SetPaused(true);
                break;
            default:
                tray.SetPaused(false);
                tray.SetStatus(TrayStatus.Error, "演示：连接被拒绝");
                break;
        }
    }
}
