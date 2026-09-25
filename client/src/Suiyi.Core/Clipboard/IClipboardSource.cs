namespace Suiyi.Core.Clipboard;

/// <summary>
/// 系统剪贴板的最小抽象。Windows 实现在 <c>Suiyi.App</c>（<c>AddClipboardFormatListener</c> 等），
/// 测试用假实现。
/// </summary>
public interface IClipboardSource
{
    /// <summary>剪贴板内容变化（<c>WM_CLIPBOARDUPDATE</c>）。可能在任意线程触发。</summary>
    event EventHandler? Changed;

    /// <summary>开始接收变化通知。重复调用无副作用。</summary>
    void StartListening();

    /// <summary>停止接收变化通知。重复调用无副作用。</summary>
    void StopListening();

    /// <summary>当前剪贴板序号（<c>GetClipboardSequenceNumber</c>），每次内容变化递增。</summary>
    uint GetSequenceNumber();

    /// <summary>
    /// 尝试读取一次文本（单次尝试，不重试）。
    /// 带隐私标记时必须返回 <see cref="ClipboardReadResult.PrivateContent"/> 且不读取正文。
    /// </summary>
    ClipboardReadResult TryReadText();

    /// <summary>写入 Unicode 文本，成功返回 <see langword="true"/>。实现可以在内部短暂重试。</summary>
    bool TrySetText(string text);
}
