namespace Suiyi.Core.Hotkeys;

/// <summary>系统全局快捷键注册的抽象。Windows 实现用 <c>RegisterHotKey</c>（<c>MOD_NOREPEAT</c>）。</summary>
public interface IHotkeyRegistrar
{
    /// <summary>已注册的快捷键被按下。</summary>
    event EventHandler? Pressed;

    /// <summary>注册（同一时刻只有一个）。失败返回 <see langword="false"/> 与系统错误码。</summary>
    bool TryRegister(HotkeyGesture gesture, out int errorCode);

    /// <summary>注销当前快捷键；没有注册时无副作用。</summary>
    void Unregister();
}
