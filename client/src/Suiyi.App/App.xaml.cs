using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Threading;
using Suiyi.App.Interop;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Hotkeys;
using Suiyi.Core.Logging;

namespace Suiyi.App;

/// <summary>
/// 组合根。后续 Issue 在 <see cref="OnStartup"/> 里创建并注册自己的组件
/// （设置、托盘、引擎进程、剪贴板、快捷键、浮窗），在 <see cref="OnExit"/> 里按相反顺序清理。
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Application 与进程同寿命，字段在 OnExit 中释放。")]
public partial class App : Application
{
    private FileLogger? _logger;
    private Win32ClipboardSource? _clipboardSource;
    private ClipboardMonitor? _clipboardMonitor;
    private Win32HotkeyRegistrar? _hotkeyRegistrar;
    private HotkeyManager? _hotkeyManager;
    private HotkeyTranslateAction? _hotkeyAction;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _logger = FileLogger.CreateDefault();
        _logger.Info($"客户端启动，版本 {typeof(App).Assembly.GetName().Version}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;

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

        // M2 骨架：显示占位窗口，关闭即退出。托盘 Issue（#29）会替换这里。
        var window = new PlaceholderWindow(_clipboardMonitor, clipboardWriter, _hotkeyManager);
        window.ApplyHotkey(GetHotkeyArgument(e.Args) ?? HotkeyParser.DefaultTranslate);
        window.Closed += (_, _) => Shutdown();
        window.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyManager?.Dispose();
        _hotkeyRegistrar?.Dispose();
        _clipboardMonitor?.Dispose();
        _clipboardSource?.Dispose();
        _logger?.Info($"客户端退出，代码 {e.ApplicationExitCode}");
        _logger?.Dispose();
        base.OnExit(e);
    }

    private static string? GetHotkeyArgument(string[] args)
    {
        var index = Array.IndexOf(args, "--hotkey");
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
