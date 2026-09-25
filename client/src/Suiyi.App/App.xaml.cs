using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Suiyi.App.Interop;
using Suiyi.App.Popup;
using Suiyi.App.Tray;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Engine;
using Suiyi.Core.Flow;
using Suiyi.Core.Hotkeys;
using Suiyi.Core.Lifecycle;
using Suiyi.Core.Logging;
using Suiyi.Core.Popup;
using Suiyi.Core.Settings;
using Suiyi.Core.Tray;

namespace Suiyi.App;

/// <summary>
/// 组合根（#34 串接）：加载设置 → 托盘（正在准备）→ 启动翻译服务 → 剪贴板监听与快捷键 → 等待事件；
/// 捕获到的文本交给 <see cref="TranslateFlowCoordinator"/>（翻译 → 浮窗）。退出时按
/// 「注销快捷键 → 停止监听 → 关闭浮窗 → 停止服务 → 隐藏托盘」顺序清理。
/// 没有主窗口：常驻托盘，托盘菜单「退出」才结束进程（ShutdownMode=OnExplicitShutdown）。
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Application 与进程同寿命，字段在 OnExit 中释放。")]
public partial class App : Application
{
    /// <summary>项目主页，显示在「关于」里。</summary>
    public const string RepositoryUrl = "https://github.com/xubin0903/suiyi";

    private const string AppTitle = "随译";

    /// <summary>复制译文时让监听忽略下一次变化的窗口。</summary>
    private static readonly TimeSpan CopySuppressWindow = TimeSpan.FromSeconds(1);

    private SingleInstanceGuard? _instanceGuard;
    private InstanceActivation? _activation;
    private FileLogger? _logger;
    private SettingsStore? _settings;
    private TrayController? _tray;
    private NotifyIconTrayView? _trayView;
    private DispatcherTimer? _trayDemoTimer;
    private FileLogger? _engineLog;
    private JobObject? _engineJob;
    private EngineClient? _engineClient;
    private EngineSupervisor? _engine;
    private TranslationService? _translation;
    private TranslateFlowCoordinator? _flow;
    private DispatcherTimer? _popupDemoTimer;
    private PopupViewModel? _popup;
    private PopupWindow? _popupWindow;
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

        // 设置（#27）：启动时读一次；托盘改动时写回。手工编辑 settings.json 后需重启生效。
        _settings = new SettingsStore(logger: _logger);
        var settings = _settings.Load();

        // 托盘（#29）。所有回调在 UI 线程上执行。
        _tray = new TrayController(TrayState.Initial with
        {
            Target = settings.PrimaryTarget,
            Paused = !settings.Clipboard.MonitorEnabled,
        });
        _trayView = new NotifyIconTrayView(_tray);
        _activation = new InstanceActivation(
            () => Dispatcher.BeginInvoke(() => _tray?.ShowNotification(AppTitle, "随译已在运行")));

        // 剪贴板监听（#30）。writer 与 monitor 共用同一个 SelfWriteTracker，自身写入不会自触发。
        var selfWrites = new SelfWriteTracker();
        _clipboardSource = new Win32ClipboardSource(_logger);
        var clipboardWriter = new ClipboardWriter(_clipboardSource, selfWrites, _logger);
        _clipboardMonitor = new ClipboardMonitor(_clipboardSource, selfWrites, settings.Clipboard.ToMonitorOptions(), logger: _logger)
        {
            Paused = !settings.Clipboard.MonitorEnabled,
        };

        // 全局快捷键（#33）。暂停剪贴板监听不影响快捷键。模拟复制引起的变化由 SuppressNext 让监听忽略。
        // 快捷键取自设置 hotkey.translate；命令行 --hotkey "Ctrl+Shift+Y" 可临时覆盖（不写回设置）；空字符串表示禁用。
        _hotkeyAction = new HotkeyTranslateAction(_clipboardSource, clipboardWriter, new Win32KeyboardInput(), logger: _logger);
        _hotkeyRegistrar = new Win32HotkeyRegistrar();
        _hotkeyManager = new HotkeyManager(_hotkeyRegistrar, _logger);
        _hotkeyManager.Pressed += (_, _) => _ = RunHotkeyActionAsync();
        _hotkeyManager.RegistrationFailed += (_, args) => _tray?.ShowNotification(AppTitle, args.Message);

        // 译文浮窗（#31），内容由主流程（#34）驱动。
        _popup = new PopupViewModel(settings.Popup.ToPopupOptions(), dispatch: action => Dispatcher.BeginInvoke(action));
        _popupWindow = new PopupWindow(_popup);
        WirePopup(_popup, clipboardWriter);

        WireTray(_tray, _clipboardMonitor);

        if (e.Args.Contains("--popup-demo"))
        {
            StartPopupDemo(_popup);
        }

        if (e.Args.Contains("--tray-demo"))
        {
            StartTrayDemo(_tray);
        }
        else
        {
            StartEngine(_tray);
            StartFlow(_engine!, _engineClient!);
        }

        // Issue #34 组合根顺序：服务启动之后才开始接收快捷键与剪贴板事件。
        _hotkeyManager.Update(GetOptionValue(e.Args, "--hotkey") ?? settings.Hotkey.Translate);
        _clipboardMonitor.Start();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _trayDemoTimer?.Stop();
        _popupDemoTimer?.Stop();

        // 1. 注销快捷键 2. 停止监听
        _hotkeyManager?.Dispose();
        _hotkeyRegistrar?.Dispose();
        _clipboardMonitor?.Dispose();

        // 3. 取消进行中的翻译并关闭浮窗
        _flow?.Dispose();
        _translation?.Dispose();
        _popupWindow?.CloseForExit();
        _popup?.Dispose();

        // 4. 停止托管的翻译服务（外部服务不动），再关闭 Job 句柄兜底
        if (_engine is { } engine && !Task.Run(engine.StopAsync).Wait(TimeSpan.FromSeconds(3)))
        {
            _logger?.Warn("停止翻译服务超时");
        }

        _engineJob?.Dispose();
        _engineClient?.Dispose();
        _engineLog?.Dispose();
        _clipboardSource?.Dispose();

        // 5. 隐藏托盘
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
            _settings?.Update(s => s with { Clipboard = s.Clipboard with { MonitorEnabled = !args.Paused } });
        };

        // 目标语言写回 primaryTarget（secondaryTarget 按 ResolveTargets 保持不同）；下一次翻译即读取新设置。
        tray.TargetChanged += (_, args) =>
        {
            _logger?.Info($"托盘：目标语言切换为 {args.Language}");
            _settings?.Update(s => s.WithPrimaryTarget(args.Language));
        };

        tray.TranslateClipboardRequested += (_, _) =>
        {
            if (_flow is null || _clipboardSource is null)
            {
                _logger?.Info("托盘：翻译剪贴板（翻译服务未启用）");
                return;
            }

            _flow.TranslateClipboard(_clipboardSource.TryReadText());
        };
        tray.ShowLastPopupRequested += (_, _) =>
        {
            if (_popup?.ShowLast() != true)
            {
                _logger?.Info("托盘：左键单击，暂无可显示的浮窗");
            }
        };

        tray.RestartEngineRequested += (_, _) =>
        {
            _logger?.Info("托盘：重启翻译服务");
            _ = _engine?.RestartAsync();
        };

        tray.OpenSettingsRequested += (_, _) => OpenSettings();
        tray.OpenLogsRequested += (_, _) => OpenFolder(LogPaths.ResolveDirectory());
        tray.AboutRequested += (_, _) => MessageBox.Show(
            $"随译 {GetVersion()}\n开源免费的本地翻译工具\n\n{RepositoryUrl}\n\n端到端延迟：{_flow?.Latency.Summary() ?? "未启用"}",
            "关于随译",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        tray.ExitRequested += (_, _) => Shutdown();
    }

    private void WirePopup(PopupViewModel popup, ClipboardWriter clipboardWriter)
    {
        // 复制译文：先 SuppressNext 再写入（写入同时登记自身序号，二者任一命中即忽略），不会再次触发翻译。
        popup.CopyTranslationRequested += (_, args) =>
        {
            clipboardWriter.SuppressNext(CopySuppressWindow);
            if (!clipboardWriter.SetText(args.Text))
            {
                clipboardWriter.CancelSuppress();
                _tray?.ShowNotification(AppTitle, "复制失败：剪贴板被其他程序占用");
            }
        };

        // 重试、指定原文语种由 TranslateFlowCoordinator 直接订阅。
    }

    private void StartFlow(EngineSupervisor engine, EngineClient client)
    {
        // 主流程（#34）：目标语言每次翻译时从设置读取，托盘切换后下一次请求即生效。
        _translation = new TranslationService(client, () => (_settings!.Current.PrimaryTarget, _settings.Current.SecondaryTarget));
        _flow = new TranslateFlowCoordinator(
            _translation,
            engine,
            _popup!,
            _tray!,
            _logger,
            dispatch: action => Dispatcher.BeginInvoke(action),

            // Loaded 优先级低于 Render：回调执行时浮窗这一帧已经渲染。
            afterRender: action => Dispatcher.BeginInvoke(action, DispatcherPriority.Loaded));

        _clipboardMonitor!.TextCaptured += (_, args) => _flow.OnTextCaptured(args.Text, args.Trigger, args.Timestamp);
        _clipboardMonitor.TextRejected += (_, args) => _flow.OnTextRejected(args.Reason, args.Length, args.Trigger);
        _hotkeyAction!.TextCaptured += (_, args) => _flow.OnTextCaptured(args.Text, args.Trigger, args.Timestamp);
        _hotkeyAction.Rejected += (_, args) => _flow.OnTextRejected(args.Reason, args.Length, args.Trigger);
    }

    private void StartPopupDemo(PopupViewModel popup)
    {
        // 跑一轮后停在最后一个状态，便于继续手测自动消失、悬停、钉住；托盘左键可重新显示。
        _logger?.Info("--popup-demo：每 3 秒切换一个浮窗状态，共一轮");
        var step = 0;
        PopupDemo.ApplyStep(popup, step);
        _popupDemoTimer = new DispatcherTimer { Interval = PopupDemo.Interval };
        _popupDemoTimer.Tick += (_, _) =>
        {
            if (++step >= PopupDemo.StepCount)
            {
                _popupDemoTimer.Stop();
                return;
            }

            PopupDemo.ApplyStep(popup, step);
        };
        _popupDemoTimer.Start();
    }

    private void StartEngine(TrayController tray)
    {
        // 翻译服务进程（#32）。配置取自设置 engine.*，再由 SUIYI_ENGINE_* 环境变量覆盖。
        var options = EngineOptionsOverrides.Apply(_settings!.Current.Engine.ToEngineOptions(), Environment.GetEnvironmentVariable);
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
            if (change.State == EngineState.Ready)
            {
                // 服务（重新）就绪后刷新 /languages 缓存。
                _engineClient?.Invalidate();
            }

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
        // 文件还不存在时先写出默认设置，方便手工编辑。
        var file = _settings!.FilePath;
        if (!File.Exists(file))
        {
            try
            {
                _settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.Error($"无法创建设置文件 {file}", ex);
                OpenFolder(Path.GetDirectoryName(file)!);
                return;
            }
        }

        StartProcess("notepad.exe", file);
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
