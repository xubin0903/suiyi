namespace Suiyi.Core.Clipboard;

/// <summary>捕获到文本的来源。</summary>
public enum ClipboardTrigger
{
    /// <summary>剪贴板监听自动捕获。</summary>
    Monitor,

    /// <summary>用户按全局快捷键手动触发（#33）。</summary>
    Hotkey,
}
