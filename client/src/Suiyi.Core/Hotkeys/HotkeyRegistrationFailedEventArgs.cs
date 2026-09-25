namespace Suiyi.Core.Hotkeys;

/// <summary>快捷键注册失败（被占用或格式非法）。</summary>
public sealed class HotkeyRegistrationFailedEventArgs(string hotkey, string reason, bool invalidFormat) : EventArgs
{
    /// <summary>设置里的原始快捷键字符串。</summary>
    public string Hotkey { get; } = hotkey;

    /// <summary>中文原因，例如「已被其他程序占用」。</summary>
    public string Reason { get; } = reason;

    /// <summary>是否因为格式非法（而不是被占用）。</summary>
    public bool InvalidFormat { get; } = invalidFormat;

    /// <summary>面向用户的提示，例如「快捷键 Ctrl+Alt+T 已被其他程序占用，请在设置中修改」。</summary>
    public string Message => $"快捷键 {Hotkey} {Reason}，请在设置中修改";
}
