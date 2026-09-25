namespace Suiyi.Core.Popup;

/// <summary>浮窗译文内容。</summary>
/// <param name="Translation">译文。</param>
/// <param name="Source">原文语种代码（自动检测时为检测结果；未知时可为 <see langword="null"/>）。</param>
/// <param name="Target">目标语种代码。</param>
public sealed record PopupResult(string Translation, string? Source, string Target)
{
    /// <summary>原文语种是否为自动检测。</summary>
    public bool SourceDetected { get; init; }

    /// <summary>翻译耗时（可选，显示为小字）。</summary>
    public TimeSpan? Elapsed { get; init; }
}
