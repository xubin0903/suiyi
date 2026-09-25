namespace Suiyi.Core.Hotkeys;

/// <summary>键盘状态与模拟输入的抽象。Windows 实现用 <c>GetAsyncKeyState</c> 与 <c>SendInput</c>。</summary>
public interface IKeyboardInput
{
    /// <summary>Ctrl、Alt、Shift、Win 中是否有任何一个仍被按住。</summary>
    bool AreModifierKeysDown();

    /// <summary>
    /// 模拟一次 Ctrl+C。系统接受了全部输入返回 <see langword="true"/>。
    /// 注意：目标窗口以更高权限运行时输入会被 UIPI 静默丢弃，返回值仍可能为 <see langword="true"/>。
    /// </summary>
    bool SendCopy();
}
