namespace Suiyi.Core.Settings;

/// <summary>需要明确告知用户（托盘气泡）的设置问题类别。一般的字段回落只写日志，不在这里。</summary>
public enum SettingsNoticeKind
{
    /// <summary><c>hotkey.region</c> 与 <c>hotkey.translate</c> 相同，框选快捷键已被禁用（#55）。</summary>
    RegionHotkeyConflict,
}

/// <summary>读取设置时产生的用户提示。</summary>
/// <param name="Kind">类别。</param>
/// <param name="Message">面向用户的中文提示（托盘气泡正文）。</param>
public sealed record SettingsNotice(SettingsNoticeKind Kind, string Message)
{
    /// <summary>框选快捷键与翻译快捷键冲突被禁用时的提示。</summary>
    /// <param name="hotkey">冲突的快捷键（规范写法或用户原文）。</param>
    public static SettingsNotice RegionHotkeyConflict(string hotkey) => new(
        SettingsNoticeKind.RegionHotkeyConflict,
        $"框选快捷键 {hotkey} 与翻译快捷键相同，框选快捷键已禁用。请在设置文件中把 hotkey.region 改为其他组合（例如 Ctrl+Alt+R），保存后重启随译");
}
