namespace Suiyi.Core.Popup;

/// <summary><see cref="PopupViewModel.Shown"/> 参数。</summary>
/// <param name="reposition">是否需要把窗口移到当前光标旁（新一次翻译且未钉住）。</param>
public sealed class PopupShownEventArgs(bool reposition) : EventArgs
{
    /// <summary>是否需要重新定位到光标旁。</summary>
    public bool Reposition { get; } = reposition;
}

/// <summary>浮窗关闭原因。</summary>
public enum PopupCloseReason
{
    /// <summary>用户按 Esc 或点击 ×。</summary>
    User,

    /// <summary>自动消失。</summary>
    AutoHide,

    /// <summary>程序关闭（如退出）。</summary>
    Program,
}

/// <summary><see cref="PopupViewModel.Closed"/> 参数。</summary>
/// <param name="reason">原因。</param>
public sealed class PopupClosedEventArgs(PopupCloseReason reason) : EventArgs
{
    /// <summary>关闭原因。</summary>
    public PopupCloseReason Reason { get; } = reason;
}

/// <summary><see cref="PopupViewModel.CopyTranslationRequested"/> 参数。</summary>
/// <param name="text">要复制的译文。</param>
public sealed class PopupCopyEventArgs(string text) : EventArgs
{
    /// <summary>译文。</summary>
    public string Text { get; } = text;
}

/// <summary><see cref="PopupViewModel.SourceLanguageOverride"/> 参数。</summary>
/// <param name="language">用户指定的原文语种代码（zh / en / ja）。</param>
public sealed class PopupSourceOverrideEventArgs(string language) : EventArgs
{
    /// <summary>原文语种代码。</summary>
    public string Language { get; } = language;
}
