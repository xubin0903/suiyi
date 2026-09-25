using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Suiyi.App.Interop;
using Suiyi.App.Tray;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Engine;
using Suiyi.Core.Hotkeys;
using Suiyi.Core.Lifecycle;
using Suiyi.Core.Logging;
using Suiyi.Core.Tray;

namespace Suiyi.App;

/// <summary>
/// 组合根。后续 Issue 在 <see cref="OnStartup"/> 里创建并注册自己的组件
/// （设置、引擎进程、浮窗），在 <see cref="OnExit"/> 里按相反顺序清理。
/// 没有主窗口：常驻托盘，托盘菜单「退出」才结束进程（ShutdownMode=OnExplicitShutdown）。
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Application 与进程同寿命，字段在 OnExit 中释放。")]
public partial class App : Application
{
    /// <summary>项目主页，显示在「关于」里。</summary>
    public const string RepositoryUrl = "https://github.com/xubin0903/suiyi";

    private const string AppTitle = "随译";

    private SingleInstanceGuard? _instanceGuard;
    private InstanceActivation? _activation;
    private FileLogger? _logger;
    private TrayController? _tray;
    private NotifyIconTrayView? _trayView;
    private DispatcherTimer? _trayDemoTimer;
    private FileLogger? _engineLog;
    private JobObject? _engineJob;
    private EngineClient? _engineClient;
    private EngineSupervisor? _engine;
    private Win32ClipboardSource? _clipboardSource;
    private ClipboardMonitor? _clipboardMonitor;
    private Win32HotkeyRegistrar? _hotkeyRegistrar;
    private HotkeyManager? _hotkeyManager;
    private HotkeyTranslateAction? _hotkeyAction;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例（#29）：已有实例时通知它弹「随译已在运行」，本进程直接退出。
        if (!SingleInstanceGuard.TryAcquire(SingleInstanceGuard.DefaultName, out _instanceGuard))
        {
            InstanceActivation.SignalExisting();
            Shutdown();
            return;
        }

        _logger = FileLogger.CreateDefault();
        _logger.Info($"客户端启动，版本 {GetVersion()}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 托盘（#29）。所有回调在 UI 线程上执行。
        _tray = new TrayController();
        _trayView = new NotifyIconTrayView(_tray);
        _activation = new InstanceActivation(
            () => Dispatcher.BeginInvoke(() => _tray?.ShowNotification(AppTitle, "随译已在运行")));

        // 剪贴板监听（#30）。writer 与 monitor 共用同一个 SelfWriteTracker，自身写入不会自触发。
        // 暂时只写日志；翻译与浮窗由集成 Issue（#34）接到 TextCaptured 上。
        var selfWrites = new SelfWriteTracker();
        _clipboardSource = new Win32ClipboardSource(_logger);
        var clipboardWriter = new ClipboardWriter(_clipboardSource, selfWrites, _logger);
        _clipboardMonitor = new ClipboardMonitor(_clipboardSource, selfWrites, logger: _logger);
        _clipboardMonitor.Start();

        // 全局快捷键（#33）。暂停剪贴板监听不影响快捷键。模拟复制引起的变化由 SuppressNext 让监听忽略。
        // 设置（#27）接入前，可用命令行 --hotkey "Ctrl+Shift+Y" 指定；空字符串表示禁用。
        _hotkeyAction = new HotkeyTranslateAction(_clipboardSource, clipboardWriter, new Win32KeyboardInput(), logger: _logger);
        _hotkeyRegistrar = new Win32HotkeyRegistrar();
        _hotkeyManager = new HotkeyManager(_hotkeyRegistrar, _logger);
        _hotkeyManager.Pressed += (_, _) => _ = RunHotkeyActionAsync();
        _hotkeyManager.RegistrationFailed += (_, args) => _tray?.ShowNotification(AppTitle, args.Message);

        WireTray(_tray, _clipboardMonitor);
        _hotkeyManager.Update(GetOptionValue(e.Args, "--hotkey") ?? HotkeyParser.DefaultTranslate);

        if (e.Args.Contains("--tray-demo"))
        {
            StartTrayDemo(_tray);
        }
        else
        {
            StartEngine(_tray);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _trayDemoTimer?.Stop();

        // 先结束托管的翻译服务（外部服务不动），再关闭 Job 句柄兜底。
        if (_engine is { } engine && !Task.Run(engine.StopAsync).Wait(TimeSpan.FromSeconds(3)))
        {
            _logger?.Warn("停止翻译服务超时");
        }

        _engineJob?.Dispose();
        _hotkeyManager?.Dispose();
        _hotkeyRegistrar?.Dispose();
        _clipboardMonitor?.Dispose();
        _clipboardSource?.Dispose();
        _engineClient?.Dispose();
        _engineLog?.Dispose();
        _activation?.Dispose();
        _trayView?.Dispose();
        _instanceGuard?.Dispose();
        _logger?.Info($"客户端退出，代码 {e.ApplicationExitCode}");
        _logger?.Dispose();
        base.OnExit(e);
    }

    private void WireTray(TrayController tray, ClipboardMonitor monitor)
    {
        tray.PauseToggled += (_, args) =>
        {
            monitor.Paused = args.Paused;
            _logger?.Info(args.Paused ? "托盘：暂停剪贴板监听" : "托盘：恢复剪贴板监听");
        };

        // 设置（#27）接入前目标语言只在内存里；#34 翻译时读取 tray.State.Target。
        tray.TargetChanged += (_, args) => _logger?.Info($"托盘：目标语言切换为 {args.Language}");

        // 以下两项由集成 Issue（#34）接到翻译与浮窗（#31）。
        tray.TranslateClipboardRequested += (_, _) => _logger?.Info("托盘：翻译剪贴板（待 #34 接线）");
        tray.ShowLastPopupRequested += (_, _) => _logger?.Info("托盘：左键单击，重新显示上次浮窗（待 #34 接线）");

        tray.RestartEngineRequested += (_, _) =>
        {
            _logger?.Info("托盘：重启翻译服务");
            _ = _engine?.RestartAsync();
        };

        tray.OpenSettingsRequested += (_, _) => OpenSettings();
        tray.OpenLogsRequested += (_, _) => OpenFolder(LogPaths.ResolveDirectory());
        tray.AboutRequested += (_, _) => MessageBox.Show(
            $"随译 {GetVersion()}\n开源免费的本地翻译工具\n\n{RepositoryUrl}",
            "关于随译",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        tray.ExitRequested += (_, _) => Shutdown();
    }

    private void StartEngine(TrayController tray)
    {
        // 翻译服务进程（#32）。配置暂取默认值 + SUIYI_ENGINE_* 环境变量，设置（#27）接入后改为从设置映射。
        var options = EngineOptionsOverrides.Apply(new EngineOptions(), Environment.GetEnvironmentVariable);
        _engineLog = new FileLogger(LogPaths.ResolveDirectory(), LogPaths.EnginePrefix);
        try
        {
            _engineJob = JobObject.CreateKillOnClose();
        }
        catch (Win32Exception ex)
        {
            _logger?.Warn("无法创建 Job Object，客户端被强杀时翻译服务可能残留", ex);
        }

        _engineClient = new EngineClient(options.Port);
        _engine = new EngineSupervisor(
            options,
            new ProcessEngineLauncher(process => _engineJob?.Assign(process)),
            new EngineClientEndpoint(_engineClient),
            logger: _logger,
            outputLogger: _engineLog);

        // 状态事件在线程池线程上触发，切回 UI 线程更新托盘。
        _engine.StateChanged += (_, change) => Dispatcher.BeginInvoke(() =>
        {
            var (status, detail) = EngineTrayStatus.Map(change);
            tray.SetStatus(status, detail);
            if (EngineTrayStatus.ShouldNotify(change))
            {
                tray.ShowNotification(AppTitle, change.Detail);
            }
        });
        _engine.Start();
    }

    private void StartTrayDemo(TrayController tray)
    {
        _logger?.Info("--tray-demo：每 2 秒轮换托盘状态");
        var step = 0;
        TrayDemo.ApplyStep(tray, step);
        _trayDemoTimer = new DispatcherTimer { Interval = TrayDemo.Interval };
        _trayDemoTimer.Tick += (_, _) => TrayDemo.ApplyStep(tray, ++step);
        _trayDemoTimer.Start();
    }

    private void OpenSettings()
    {
        // 与设置 Issue（#27）约定的位置；文件还不存在时打开所在目录。
        var directory = Environment.GetEnvironmentVariable("SUIYI_CONFIG_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "suiyi");
        var file = Path.Combine(directory, "settings.json");
        if (File.Exists(file))
        {
            StartProcess("notepad.exe", file);
        }
        else
        {
            OpenFolder(directory);
        }
    }

    private void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn($"无法创建目录 {directory}：{ex.Message}");
        }

        StartProcess("explorer.exe", directory);
    }

    private void StartProcess(string fileName, string argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName) { ArgumentList = { argument }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger?.Error($"无法启动 {fileName} {argument}", ex);
            _tray?.ShowNotification(AppTitle, $"无法打开 {argument}");
        }
    }

    private static string GetVersion() => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static string? GetOptionValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private async Task RunHotkeyActionAsync()
    {
        try
        {
            await _hotkeyAction!.ExecuteAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 快捷键回调不能让异常逃逸到消息循环。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger?.Error("快捷键：取词时出现异常", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("UI 线程未处理异常", e.Exception);
    }
}
