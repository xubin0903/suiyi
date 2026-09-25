namespace Suiyi.Core.Clipboard;

/// <summary>剪贴板内容未被接受的原因。只用于日志与轻提示，不携带正文。</summary>
public enum RejectReason
{
    /// <summary>空白。</summary>
    Empty,

    /// <summary>短于 <see cref="ClipboardFilterOptions.MinChars"/>。</summary>
    TooShort,

    /// <summary>长于 <see cref="ClipboardFilterOptions.MaxChars"/>，上层可决定是否提示。</summary>
    TooLong,

    /// <summary>纯数字、金额、日期、电话样式。</summary>
    NumericLike,

    /// <summary>没有任何文字，只有标点、符号或 emoji。</summary>
    SymbolsOnly,

    /// <summary>单个网址。</summary>
    Url,

    /// <summary>单个邮箱地址。</summary>
    Email,

    /// <summary>单个 Windows 或 Unix 文件路径。</summary>
    FilePath,

    /// <summary>GUID 或十六进制哈希样式。</summary>
    HexOrGuid,

    /// <summary>疑似代码块。</summary>
    CodeLike,

    /// <summary>与上一次被接受的文本相同，且间隔短于去重窗口。</summary>
    Duplicate,

    /// <summary>剪贴板里没有文本格式（图片、文件列表等）。</summary>
    NoText,

    /// <summary>剪贴板标记为不应被监听或记录（密码管理器等），未读取正文。</summary>
    PrivateContent,

    /// <summary>剪贴板被其他程序占用，重试后仍无法打开。</summary>
    ClipboardBusy,
}
