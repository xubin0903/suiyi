using System.Windows;
using Suiyi.Core.Clipboard;
using Suiyi.Core.Hotkeys;

namespace Suiyi.App;

/// <summary>骨架阶段的占位窗口，附带剪贴板监听与快捷键的手测控件。托盘 Issue（#29）会移除它。</summary>
public partial class PlaceholderWindow : Window
{
    private readonly ClipboardMonitor _monitor;
    private readonly ClipboardWriter _writer;
    private readonly HotkeyManager _hotkeys;

    /// <summary>创建占位窗口。</summary>
    public PlaceholderWindow(ClipboardMonitor monitor, ClipboardWriter writer, HotkeyManager hotkeys)
    {
        _monitor = monitor;
        _writer = writer;
        _hotkeys = hotkeys;
        InitializeComponent();
        _hotkeys.RegistrationFailed += (_, e) => HotkeyStatusText.Text = e.Message;
    }

    /// <summary>应用快捷键设置并刷新状态文字。</summary>
    public void ApplyHotkey(string hotkey)
    {
        HotkeyTextBox.Text = hotkey;
        if (_hotkeys.Update(hotkey))
        {
            HotkeyStatusText.Text = _hotkeys.Current is { } current ? $"快捷键 {current} 已启用" : "快捷键已禁用";
        }
    }

    private void OnPauseChanged(object sender, RoutedEventArgs e) =>
        _monitor.Paused = PauseCheckBox.IsChecked == true;

    private void OnWriteTestText(object sender, RoutedEventArgs e) =>
        _writer.SetText("随译剪贴板写入测试：这段文字不应触发翻译。");

    private void OnApplyHotkey(object sender, RoutedEventArgs e) => ApplyHotkey(HotkeyTextBox.Text);
}
