using System.Windows;
using Suiyi.Core.Clipboard;

namespace Suiyi.App;

/// <summary>骨架阶段的占位窗口，附带剪贴板监听的手测控件。托盘 Issue（#29）会移除它。</summary>
public partial class PlaceholderWindow : Window
{
    private readonly ClipboardMonitor _monitor;
    private readonly ClipboardWriter _writer;

    /// <summary>创建占位窗口。</summary>
    public PlaceholderWindow(ClipboardMonitor monitor, ClipboardWriter writer)
    {
        _monitor = monitor;
        _writer = writer;
        InitializeComponent();
    }

    private void OnPauseChanged(object sender, RoutedEventArgs e) =>
        _monitor.Paused = PauseCheckBox.IsChecked == true;

    private void OnWriteTestText(object sender, RoutedEventArgs e) =>
        _writer.SetText("随译剪贴板写入测试：这段文字不应触发翻译。");
}
