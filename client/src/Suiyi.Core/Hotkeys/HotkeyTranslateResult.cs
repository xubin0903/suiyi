using Suiyi.Core.Clipboard;

namespace Suiyi.Core.Hotkeys;

/// <summary>一次快捷键触发的结果状态。</summary>
public enum HotkeyTranslateStatus
{
    /// <summary>捕获到文本并已发出 <see cref="HotkeyTranslateAction.TextCaptured"/>。</summary>
    Captured,

    /// <summary>没有可翻译的文本，已发出 <see cref="HotkeyTranslateAction.Rejected"/>。</summary>
    Rejected,

    /// <summary>上一次触发尚未结束，本次忽略。</summary>
    AlreadyRunning,
}

/// <summary>一次快捷键触发的结果。</summary>
/// <param name="Status">状态。</param>
/// <param name="Text">捕获的文本（仅 <see cref="HotkeyTranslateStatus.Captured"/>）。</param>
/// <param name="Reason">拒绝原因（仅 <see cref="HotkeyTranslateStatus.Rejected"/>）。</param>
/// <param name="CopiedSelection">模拟复制是否引起了剪贴板变化（即取到了选中文本）；否则为回退到当前剪贴板。</param>
public readonly record struct HotkeyTranslateResult(
    HotkeyTranslateStatus Status,
    string? Text,
    RejectReason? Reason,
    bool CopiedSelection);
