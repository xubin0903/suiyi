namespace Suiyi.Core.Engine;

/// <summary>引擎调用失败的类别。调用方主动取消不属于错误，以 <see cref="OperationCanceledException"/> 抛出。</summary>
public enum EngineErrorKind
{
    /// <summary>连接被拒绝或被重置：服务未启动或已退出。</summary>
    Unavailable,

    /// <summary>超过 <see cref="TimeoutPolicy"/> 给出的超时。</summary>
    Timeout,

    /// <summary><c>unsupported_pair</c>：语向没有可加载的模型。</summary>
    UnsupportedPair,

    /// <summary><c>text_too_long</c>：文本超过服务的字符上限。</summary>
    TextTooLong,

    /// <summary><c>detect_failed</c>：自动检测不出可用语种。</summary>
    DetectFailed,

    /// <summary><c>invalid_request</c>：请求字段非法。</summary>
    InvalidRequest,

    /// <summary><c>image_too_large</c>（413）：截图超过字节或像素上限；客户端预检拦截时也是此类（无状态码）。⚠ #53 草案。</summary>
    ImageTooLarge,

    /// <summary><c>unsupported_media_type</c>（415）：请求体不是 PNG（服务端按魔数判定）。⚠ #53 草案。</summary>
    UnsupportedMediaType,

    /// <summary><c>invalid_image</c>（422）：PNG 无法解码。⚠ #53 草案。</summary>
    InvalidImage,

    /// <summary><c>ocr_unavailable</c>（503）：OCR 模型缺失。⚠ #53 草案。</summary>
    OcrUnavailable,

    /// <summary><c>internal_error</c>，或 5xx 且正文不是错误信封。</summary>
    Internal,

    /// <summary>其他：非信封的 4xx（如框架 404）、无法解析的 JSON 等。</summary>
    Unknown,
}
