namespace Suiyi.Core.Tray;

/// <summary>
/// 托盘的界面无关实现：维护 <see cref="TrayState"/>，把菜单命令翻译成 <see cref="ITrayService"/> 的事件。
/// <c>Suiyi.App</c> 的 NotifyIcon 视图订阅 <see cref="StateChanged"/> / <see cref="NotificationRequested"/> 渲染，
/// 并把用户点击交给 <see cref="Invoke(TrayMenuItem)"/> / <see cref="OnLeftClick"/>。
/// </summary>
public sealed class TrayController : ITrayService
{
    private TrayState _state;

    /// <summary>创建控制器。</summary>
    public TrayController(TrayState? initial = null)
    {
        _state = initial ?? TrayState.Initial;
    }

    /// <inheritdoc />
    public event EventHandler? TranslateClipboardRequested;

    /// <inheritdoc />
    public event EventHandler<TrayPauseToggledEventArgs>? PauseToggled;

    /// <inheritdoc />
    public event EventHandler? TranslateRegionRequested;

    /// <inheritdoc />
    public event EventHandler<TrayTargetChangedEventArgs>? TargetChanged;

    /// <inheritdoc />
    public event EventHandler? RestartEngineRequested;

    /// <inheritdoc />
    public event EventHandler<TrayGlossaryToggledEventArgs>? GlossaryToggled;

    /// <inheritdoc />
    public event EventHandler? EditGlossaryRequested;

    /// <inheritdoc />
    public event EventHandler? ReloadGlossaryRequested;

    /// <summary>右键菜单即将打开（视图在重建菜单前调用 <see cref="NotifyMenuOpening"/>）：集成层可借机刷新状态行。</summary>
    public event EventHandler? MenuOpening;

    /// <inheritdoc />
    public event EventHandler? OpenSettingsRequested;

    /// <inheritdoc />
    public event EventHandler? OpenLogsRequested;

    /// <inheritdoc />
    public event EventHandler? AboutRequested;

    /// <inheritdoc />
    public event EventHandler? ExitRequested;

    /// <inheritdoc />
    public event EventHandler? ShowLastPopupRequested;

    /// <summary>显示状态变化，视图据此刷新图标、提示和菜单。</summary>
    public event EventHandler? StateChanged;

    /// <summary>请求显示气泡通知。</summary>
    public event EventHandler<TrayNotificationEventArgs>? NotificationRequested;

    /// <inheritdoc />
    public TrayState State => _state;

    /// <summary>当前菜单模型。</summary>
    public IReadOnlyList<TrayMenuItem> Menu => TrayMenuBuilder.Build(_state);

    /// <inheritdoc />
    public void SetStatus(TrayStatus status, string? detail = null) =>
        Update(_state with { Status = status, Detail = detail });

    /// <inheritdoc />
    public void SetPaused(bool paused) => Update(_state with { Paused = paused });

    /// <inheritdoc />
    public void SetRegionHotkey(string? hotkey) =>
        Update(_state with { RegionHotkey = string.IsNullOrWhiteSpace(hotkey) ? null : hotkey.Trim() });

    /// <inheritdoc />
    public void SetTarget(string language)
    {
        if (!TrayLanguages.IsSupported(language))
        {
            throw new ArgumentException($"不支持的目标语言：{language}", nameof(language));
        }

        Update(_state with { Target = language.ToLowerInvariant() });
    }

    /// <inheritdoc />
    public void SetGlossaryEnabled(bool enabled) => Update(_state with { GlossaryEnabled = enabled });

    /// <inheritdoc />
    public void SetGlossaryStatus(string? status) =>
        Update(_state with { GlossaryStatus = string.IsNullOrWhiteSpace(status) ? Glossary.GlossaryStatusText.Unknown : status.Trim() });

    /// <summary>视图在右键菜单打开、重建菜单之前调用。</summary>
    public void NotifyMenuOpening() => MenuOpening?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void ShowNotification(string title, string message) =>
        NotificationRequested?.Invoke(this, new TrayNotificationEventArgs(title, message));

    /// <summary>执行一个菜单项（由视图在用户点击时调用）。</summary>
    public void Invoke(TrayMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsEnabled && !item.IsSeparator)
        {
            Invoke(item.Command, item.Argument);
        }
    }

    /// <summary>执行一个命令。<see cref="TrayCommand.TogglePause"/> 与 <see cref="TrayCommand.SetTarget"/> 会先更新状态再发事件。</summary>
    public void Invoke(TrayCommand command, string? argument = null)
    {
        switch (command)
        {
            case TrayCommand.TranslateClipboard:
                TranslateClipboardRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.TranslateRegion:
                TranslateRegionRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.TogglePause:
                var paused = !_state.Paused;
                SetPaused(paused);
                PauseToggled?.Invoke(this, new TrayPauseToggledEventArgs(paused));
                break;
            case TrayCommand.SetTarget:
                if (argument is null || !TrayLanguages.IsSupported(argument))
                {
                    return;
                }

                var language = argument.ToLowerInvariant();
                if (string.Equals(language, _state.Target, StringComparison.Ordinal))
                {
                    return;
                }

                SetTarget(language);
                TargetChanged?.Invoke(this, new TrayTargetChangedEventArgs(language));
                break;
            case TrayCommand.RestartEngine:
                RestartEngineRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.ToggleGlossary:
                var glossary = !_state.GlossaryEnabled;
                SetGlossaryEnabled(glossary);
                GlossaryToggled?.Invoke(this, new TrayGlossaryToggledEventArgs(glossary));
                break;
            case TrayCommand.EditGlossary:
                EditGlossaryRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.ReloadGlossary:
                ReloadGlossaryRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.OpenSettings:
                OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.OpenLogs:
                OpenLogsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.About:
                AboutRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.Exit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.None:
            default:
                break;
        }
    }

    /// <summary>左键单击托盘图标。</summary>
    public void OnLeftClick() => ShowLastPopupRequested?.Invoke(this, EventArgs.Empty);

    private void Update(TrayState next)
    {
        if (next == _state)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
