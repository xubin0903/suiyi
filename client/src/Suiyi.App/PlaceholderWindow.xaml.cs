using System.Windows;

namespace Suiyi.App;

/// <summary>骨架阶段的占位窗口。托盘 Issue（#29）会移除它。</summary>
public partial class PlaceholderWindow : Window
{
    /// <summary>创建占位窗口。</summary>
    public PlaceholderWindow()
    {
        InitializeComponent();
    }
}
