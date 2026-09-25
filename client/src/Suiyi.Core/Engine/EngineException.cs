using System.Globalization;

namespace Suiyi.Core.Engine;

/// <summary>引擎调用失败。<see cref="Kind"/> 用于分支处理，<see cref="UserMessage"/> 可直接显示给用户。</summary>
public sealed class EngineException : Exception
{
    /// <summary>创建引擎错误。一般由 <see cref="EngineClient"/> 构造。</summary>
    /// <param name="kind">错误类别。</param>
    /// <param name="message">诊断信息（写日志用，可能含服务端原文，不含用户正文）。</param>
    /// <param name="innerException">底层异常。</param>
    public EngineException(EngineErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>错误类别。</summary>
    public EngineErrorKind Kind { get; }

    /// <summary>HTTP 状态码；没有收到响应时为 <see langword="null"/>。</summary>
    public int? StatusCode { get; init; }

    /// <summary>服务端 <c>error.code</c>；不是错误信封时为 <see langword="null"/>。</summary>
    public string? ErrorCode { get; init; }

    /// <summary><see cref="EngineErrorKind.UnsupportedPair"/> 时缺失的模型 id（可能为空）。</summary>
    public IReadOnlyList<string> MissingModels { get; init; } = [];

    /// <summary><see cref="EngineErrorKind.UnsupportedPair"/> 时请求的原文语种。</summary>
    public string? SourceLanguage { get; init; }

    /// <summary><see cref="EngineErrorKind.UnsupportedPair"/> 时请求的目标语种。</summary>
    public string? TargetLanguage { get; init; }

    /// <summary><see cref="EngineErrorKind.TextTooLong"/> 时服务的字符上限。</summary>
    public int? Limit { get; init; }

    /// <summary><see cref="EngineErrorKind.TextTooLong"/> 时文本长度。</summary>
    public int? Length { get; init; }

    /// <summary><see cref="EngineErrorKind.Timeout"/> 时生效的超时。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>面向用户的中文短提示。</summary>
    public string UserMessage => Kind switch
    {
        EngineErrorKind.Unavailable => "翻译服务未运行或已退出",
        EngineErrorKind.Timeout => Timeout is { } t
            ? string.Create(CultureInfo.InvariantCulture, $"翻译超时（{(int)t.TotalMilliseconds} 毫秒），请重试")
            : "翻译超时，请重试",
        EngineErrorKind.UnsupportedPair => MissingModels.Count > 0
            ? "未安装语向模型：" + string.Join("、", MissingModels)
            : $"不支持该语向：{SourceLanguage ?? "?"}→{TargetLanguage ?? "?"}",
        EngineErrorKind.TextTooLong => Limit is { } limit && Length is { } length
            ? string.Create(CultureInfo.InvariantCulture, $"文本过长：{length} 字，上限 {limit} 字")
            : "文本过长",
        EngineErrorKind.DetectFailed => "无法识别原文语种，请手动指定",
        EngineErrorKind.InvalidRequest => "翻译请求无效",
        EngineErrorKind.Internal => "翻译服务内部错误",
        _ => "翻译服务返回了无法识别的响应",
    };
}
