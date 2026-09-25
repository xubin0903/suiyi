using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Suiyi.Core.Tray;

namespace Suiyi.App.Tray;

/// <summary>
/// 用 WinForms <see cref="NotifyIcon"/> 渲染 <see cref="TrayController"/>：图标与悬停提示随状态刷新，
/// 右键菜单在每次打开时按 <see cref="TrayMenuBuilder"/> 重建，点击交回控制器。
/// </summary>
internal sealed class NotifyIconTrayView : IDisposable
{
    private const int BalloonTimeoutMs = 3000;

    private readonly TrayController _controller;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<TrayStatus, Icon> _icons = [];
    private bool _disposed;

    public NotifyIconTrayView(TrayController controller)
    {
        _controller = controller;
        var iconSize = SystemInformation.SmallIconSize;
        foreach (var status in Enum.GetValues<TrayStatus>())
        {
            _icons[status] = TrayIconRenderer.Create(status, iconSize);
        }

        _menu = new ContextMenuStrip();
        _menu.Opening += OnMenuOpening;

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
        };
        _notifyIcon.MouseClick += OnMouseClick;

        _controller.StateChanged += OnStateChanged;
        _controller.NotificationRequested += OnNotificationRequested;
        Render();
        _notifyIcon.Visible = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.StateChanged -= OnStateChanged;
        _controller.NotificationRequested -= OnNotificationRequested;

        // 先隐藏再释放，避免任务栏残留「幽灵图标」。
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        ClearMenu();
        _menu.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        var state = _controller.State;
        _notifyIcon.Icon = _icons[state.Effective];
        _notifyIcon.Text = state.ToolTip;
    }

    private void OnNotificationRequested(object? sender, TrayNotificationEventArgs e) =>
        _notifyIcon.ShowBalloonTip(BalloonTimeoutMs, e.Title, e.Message, ToolTipIcon.Info);

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _controller.OnLeftClick();
        }
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        ClearMenu();
        foreach (var item in _controller.Menu)
        {
            _menu.Items.Add(CreateItem(item));
        }

        // 菜单原本为空时 WinForms 会预设 Cancel=true。
        e.Cancel = false;
    }

    private ToolStripItem CreateItem(TrayMenuItem model)
    {
        if (model.IsSeparator)
        {
            return new ToolStripSeparator();
        }

        var item = new ToolStripMenuItem(model.Text)
        {
            Enabled = model.IsEnabled,
            Checked = model.IsChecked,
        };

        if (model.Children.Count > 0)
        {
            foreach (var child in model.Children)
            {
                item.DropDownItems.Add(CreateItem(child));
            }
        }
        else if (model.IsEnabled)
        {
            item.Click += (_, _) => _controller.Invoke(model);
        }

        return item;
    }

    private void ClearMenu()
    {
        var old = _menu.Items.Cast<ToolStripItem>().ToArray();
        _menu.Items.Clear();
        foreach (var item in old)
        {
            item.Dispose();
        }
    }
}
