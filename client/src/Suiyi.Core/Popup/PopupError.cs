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

    /// <summary>等待翻译服务就绪超时（#50）。</summary>
    EngineStartTimeout,

    /// <summary>框选区域过大（#53 草案 <c>image_too_large</c>）。重试同一选区不会成功，不显示重试。</summary>
    ImageTooLarge,

    /// <summary>截图无法识别（#53 草案 <c>unsupported_media_type</c> / <c>invalid_image</c>）。</summary>
    InvalidImage,

    /// <summary>OCR 模型未安装（#53 草案 <c>ocr_unavailable</c>）。</summary>
    OcrUnavailable,
}

/// <summary>浮窗错误内容。</summary>
/// <param name="Kind">类别。</param>
public sealed record PopupError(PopupErrorKind Kind)
{
    /// <summary>缺失的模型 id（<see cref="PopupErrorKind.MissingModels"/>、<see cref="PopupErrorKind.OcrUnavailable"/>）。</summary>
    public IReadOnlyList<string> MissingModels { get; init; } = [];

    /// <summary>字符上限（<see cref="PopupErrorKind.TextTooLong"/>）。</summary>
    public int? Limit { get; init; }

    /// <summary>实际字符数（<see cref="PopupErrorKind.TextTooLong"/>）。</summary>
    public int? Length { get; init; }

    /// <summary>
    /// OCR 不可用的原因（<see cref="PopupErrorKind.OcrUnavailable"/>）：503 <c>details.reason</c>，没有时取 <c>/health.ocr_error.reason</c>。
    /// 取值见 <see cref="OcrUnavailableReasons"/>；未知或为空时按模型缺失处理。
    /// </summary>
    public string? OcrReason { get; init; }

    /// <summary>补充说明（<see cref="PopupErrorKind.Other"/>、<see cref="PopupErrorKind.ServiceUnavailable"/> 时代替默认文案）。</summary>
    public string? Detail { get; init; }

    /// <summary>面向用户的中文短提示。</summary>
    public string Message => Kind switch
    {
        PopupErrorKind.ServiceUnavailable => string.IsNullOrWhiteSpace(Detail) ? "翻译服务未运行或已退出，点「重试」会重启翻译服务" : Detail.Trim(),
        PopupErrorKind.Timeout => "翻译超时，请重试",
        PopupErrorKind.MissingModels => MissingModels.Count > 0
            ? "未安装语向模型：" + string.Join("、", MissingModels)
            : "未安装该语向的模型",
        PopupErrorKind.DetectFailed => "无法识别原文语种，请点击语种标签手动指定",
        PopupErrorKind.TextTooLong => Limit is { } limit && Length is { } length
            ? string.Create(CultureInfo.InvariantCulture, $"文本过长：{length} 字，上限 {limit} 字")
            : "文本过长",
        PopupErrorKind.EngineStartTimeout => "翻译服务启动超时，点「重试」会重启翻译服务",
        PopupErrorKind.ImageTooLarge => "选区过大，请缩小选区后重新框选",
        PopupErrorKind.InvalidImage => "截图无法识别，请重新框选",
        PopupErrorKind.OcrUnavailable => OcrReason switch
        {
            OcrUnavailableReasons.DependencyMissing => "OCR 组件未安装",
            OcrUnavailableReasons.ModelsInvalid => "OCR 模型文件不完整或已损坏",
            OcrUnavailableReasons.ManifestUnavailable => "OCR 模型清单不可用",
            _ => MissingModels.Count > 0 ? "OCR 模型未安装：" + string.Join("、", MissingModels) : "OCR 模型未安装",
        },
        _ => string.IsNullOrWhiteSpace(Detail) ? "翻译失败，请重试" : Detail.Trim(),
    };

    /// <summary>
    /// 第二行：告诉用户怎么修（目前只有 <see cref="PopupErrorKind.OcrUnavailable"/>：下载模型或安装 OCR 依赖的命令）。没有时为 <see langword="null"/>。
    /// </summary>
    public string? Hint => Kind switch
    {
        PopupErrorKind.OcrUnavailable => OcrReason switch
        {
            OcrUnavailableReasons.DependencyMissing => $"请在随译仓库根目录运行 {PopupText.OcrInstallCommand}，然后在托盘点「重启翻译服务」",
            OcrUnavailableReasons.ManifestUnavailable => "请更新随译源码（git pull）后重启随译，详情见日志",
            OcrUnavailableReasons.ModelsInvalid => $"请在随译仓库根目录运行 {PopupText.OcrDownloadCommand} 重新下载，完成后点「重试」",
            _ => $"请在随译仓库根目录运行 {PopupText.OcrDownloadCommand} 下载，完成后点「重试」",
        },
        _ => null,
    };

    /// <summary>是否显示「重试」。文本过长、选区过大重试也不会成功，不显示。</summary>
    public bool CanRetry => Kind is not (PopupErrorKind.TextTooLong or PopupErrorKind.ImageTooLarge);
}

/// <summary><c>ocr_unavailable</c> 的 <c>details.reason</c> / <c>/health.ocr_error.reason</c>（docs/engine/HTTP-API.md）。</summary>
public static class OcrUnavailableReasons
{
    /// <summary>没装 <c>engine[ocr]</c>。</summary>
    public const string DependencyMissing = "dependency_missing";

    /// <summary>缺 OCR 模型文件。</summary>
    public const string ModelsMissing = "models_missing";

    /// <summary>模型文件字节数或 sha256 不符。</summary>
    public const string ModelsInvalid = "models_invalid";

    /// <summary>OCR 模型清单读不到。</summary>
    public const string ManifestUnavailable = "manifest_unavailable";
}
