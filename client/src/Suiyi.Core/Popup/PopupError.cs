using System.Globalization;

namespace Suiyi.Core.Popup;

/// <summary>浮窗显示用的错误类别（不依赖 HTTP 客户端类型，由集成方映射）。</summary>
public enum PopupErrorKind
{
    /// <summary>翻译服务未运行或已退出。</summary>
    ServiceUnavailable,

    /// <summary>翻译超时。</summary>
    Timeout,

    /// <summary>未安装该语向的模型。</summary>
    MissingModels,

    /// <summary>无法识别原文语种。</summary>
    DetectFailed,

    /// <summary>文本过长。</summary>
    TextTooLong,

    /// <summary>其他错误（服务内部错误、无效请求等）。</summary>
    Other,
}

/// <summary>浮窗错误内容。</summary>
/// <param name="Kind">类别。</param>
public sealed record PopupError(PopupErrorKind Kind)
{
    /// <summary>缺失的模型 id（<see cref="PopupErrorKind.MissingModels"/>）。</summary>
    public IReadOnlyList<string> MissingModels { get; init; } = [];

    /// <summary>字符上限（<see cref="PopupErrorKind.TextTooLong"/>）。</summary>
    public int? Limit { get; init; }

    /// <summary>实际字符数（<see cref="PopupErrorKind.TextTooLong"/>）。</summary>
    public int? Length { get; init; }

    /// <summary>补充说明（<see cref="PopupErrorKind.Other"/> 时代替默认文案）。</summary>
    public string? Detail { get; init; }

    /// <summary>面向用户的中文短提示。</summary>
    public string Message => Kind switch
    {
        PopupErrorKind.ServiceUnavailable => "翻译服务未运行或已退出",
        PopupErrorKind.Timeout => "翻译超时，请重试",
        PopupErrorKind.MissingModels => MissingModels.Count > 0
            ? "未安装语向模型：" + string.Join("、", MissingModels)
            : "未安装该语向的模型",
        PopupErrorKind.DetectFailed => "无法识别原文语种，请点击语种标签手动指定",
        PopupErrorKind.TextTooLong => Limit is { } limit && Length is { } length
            ? string.Create(CultureInfo.InvariantCulture, $"文本过长：{length} 字，上限 {limit} 字")
            : "文本过长",
        _ => string.IsNullOrWhiteSpace(Detail) ? "翻译失败，请重试" : Detail.Trim(),
    };

    /// <summary>是否显示「重试」。文本过长重试也不会成功，不显示。</summary>
    public bool CanRetry => Kind != PopupErrorKind.TextTooLong;
}
