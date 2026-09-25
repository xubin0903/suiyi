using Suiyi.Core.Logging;

namespace Suiyi.Core.Hotkeys;

/// <summary>
/// 管理一个全局快捷键（「翻译」或「框选翻译」各一个实例）的注册：解析设置字符串、注册 / 重新注册、失败时发 <see cref="RegistrationFailed"/>（不抛异常）。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    /// <summary>Win32 <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>。</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly IHotkeyRegistrar _registrar;
    private readonly IAppLogger _logger;
    private readonly string _label;
    private bool _disposed;

    /// <summary>创建管理器；此时尚未注册任何快捷键，调用 <see cref="Update"/> 注册。</summary>
    /// <param name="registrar">系统注册器（每个管理器独占一个）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="label">日志与提示里的名称，例如「快捷键」「框选快捷键」。</param>
    public HotkeyManager(IHotkeyRegistrar registrar, IAppLogger? logger = null, string label = "快捷键")
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _logger = logger ?? NullAppLogger.Instance;
        _label = string.IsNullOrWhiteSpace(label) ? "快捷键" : label;
        _registrar.Pressed += OnPressed;
    }

    /// <summary>当前生效的快捷键被按下（暂停剪贴板监听时同样触发）。</summary>
    public event EventHandler? Pressed;

    /// <summary>注册失败：格式非法或被其他程序占用。集成层据此用托盘气泡提示。</summary>
    public event EventHandler<HotkeyRegistrationFailedEventArgs>? RegistrationFailed;

    /// <summary>当前已注册的快捷键；未注册（禁用或失败）时为 <see langword="null"/>。</summary>
    public HotkeyGesture? Current { get; private set; }

    /// <summary>
    /// 按设置字符串（重新）注册。空字符串表示禁用。
    /// 新快捷键与当前相同时不重复注册。失败时旧快捷键已注销，<see cref="Current"/> 为 <see langword="null"/>。
    /// </summary>
    /// <returns>注册成功或已禁用返回 <see langword="true"/>。</returns>
    public bool Update(string? hotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var text = hotkey ?? string.Empty;

        if (!HotkeyParser.TryParse(text, out var gesture, out var error))
        {
            UnregisterCurrent();
            _logger.Warn($"{_label}：「{text}」格式无效：{error}");
            RegistrationFailed?.Invoke(this, new HotkeyRegistrationFailedEventArgs(text, $"格式无效（{error}）", invalidFormat: true, _label));
            return false;
        }

        if (gesture is null)
        {
            UnregisterCurrent();
            _logger.Info($"{_label}：已禁用");
            return true;
        }

        if (Current == gesture)
        {
            return true;
        }

        UnregisterCurrent();
        if (_registrar.TryRegister(gesture.Value, out var errorCode))
        {
            Current = gesture;
            _logger.Info($"{_label}：已注册 {gesture}");
            return true;
        }

        var reason = errorCode == ErrorHotkeyAlreadyRegistered
            ? "已被其他程序占用"
            : $"注册失败（错误码 {errorCode}）";
        _logger.Warn($"{_label}：{gesture} {reason}");
        RegistrationFailed?.Invoke(this, new HotkeyRegistrationFailedEventArgs(gesture.Value.ToString(), reason, invalidFormat: false, _label));
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterCurrent();
        _registrar.Pressed -= OnPressed;
        _disposed = true;
    }

    private void UnregisterCurrent()
    {
        if (Current is null)
        {
            return;
        }

        _registrar.Unregister();
        Current = null;
    }

    private void OnPressed(object? sender, EventArgs e)
    {
        if (Current is not null)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
    }
}
