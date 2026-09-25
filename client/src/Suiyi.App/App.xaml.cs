using System.Windows;
using System.Windows.Threading;
using Suiyi.Core.Logging;

namespace Suiyi.App;

/// <summary>
/// 组合根。后续 Issue 在 <see cref="OnStartup"/> 里创建并注册自己的组件
/// （设置、托盘、引擎进程、剪贴板、快捷键、浮窗），在 <see cref="OnExit"/> 里按相反顺序清理。
/// </summary>
public partial class App : Application
{
    private FileLogger? _logger;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _logger = FileLogger.CreateDefault();
        _logger.Info($"客户端启动，版本 {typeof(App).Assembly.GetName().Version}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // M2 骨架：显示占位窗口，关闭即退出。托盘 Issue（#29）会替换这里。
        var window = new PlaceholderWindow();
        window.Closed += (_, _) => Shutdown();
        window.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Info($"客户端退出，代码 {e.ApplicationExitCode}");
        _logger?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("UI 线程未处理异常", e.Exception);
    }
}
