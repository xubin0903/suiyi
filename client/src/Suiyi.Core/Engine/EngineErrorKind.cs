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

    /// <summary><c>internal_error</c>，或 5xx 且正文不是错误信封。</summary>
    Internal,

    /// <summary>其他：非信封的 4xx（如框架 404）、无法解析的 JSON 等。</summary>
    Unknown,
}
