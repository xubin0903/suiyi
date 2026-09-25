using Suiyi.Core.Engine;
using Suiyi.Core.Ocr;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Flow;

/// <summary><see cref="EngineException"/> → 浮窗错误（纯函数）。</summary>
public static class PopupErrorMapper
{
    /// <summary>翻译服务启动失败时的浮窗提示。</summary>
    public const string EngineFailedMessage = "翻译服务启动失败，点「重试」会重启翻译服务";

    /// <summary>映射引擎错误。</summary>
    /// <param name="exception">引擎调用失败。</param>
    /// <param name="ocrHealth">最近一次 <c>/health.ocr_error</c>：<c>ocr_unavailable</c> 的 <c>details</c> 缺原因或缺失模型时用它补上。</param>
    public static PopupError Map(EngineException exception, OcrHealthError? ocrHealth = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Kind switch
        {
            EngineErrorKind.Unavailable => new PopupError(PopupErrorKind.ServiceUnavailable),
            EngineErrorKind.Timeout => new PopupError(PopupErrorKind.Timeout),
            EngineErrorKind.UnsupportedPair => new PopupError(PopupErrorKind.MissingModels)
            {
                MissingModels = exception.MissingModels,
            },
            EngineErrorKind.TextTooLong => new PopupError(PopupErrorKind.TextTooLong) { Limit = exception.Limit, Length = exception.Length },
            EngineErrorKind.DetectFailed => new PopupError(PopupErrorKind.DetectFailed),
            // OCR 错误码（#53 草案）统一走 OcrResultMapper，文案只维护一处；客户端预检拦截的 ImageTooLarge 也带同形 Details。
            EngineErrorKind.ImageTooLarge or EngineErrorKind.UnsupportedMediaType or EngineErrorKind.InvalidImage or EngineErrorKind.OcrUnavailable =>
                MapOcr(exception, ocrHealth),
            _ => new PopupError(PopupErrorKind.Other) { Detail = exception.UserMessage },
        };
    }

    private static PopupError MapOcr(EngineException exception, OcrHealthError? ocrHealth)
    {
        // 没有错误码（不是来自服务端信封）时按类别补上；Details 缺字段时用异常上已解析的值兜底。
        var code = exception.ErrorCode ?? exception.Kind switch
        {
            EngineErrorKind.ImageTooLarge => OcrErrorCodes.ImageTooLarge,
            EngineErrorKind.UnsupportedMediaType => OcrErrorCodes.UnsupportedMediaType,
            EngineErrorKind.InvalidImage => OcrErrorCodes.InvalidImage,
            _ => OcrErrorCodes.OcrUnavailable,
        };
        var mapped = OcrResultMapper.MapError(code, exception.Details);
        if (mapped.Kind == PopupErrorKind.OcrUnavailable && ocrHealth is not null)
        {
            mapped = mapped with
            {
                OcrReason = mapped.OcrReason ?? ocrHealth.Reason,
                MissingModels = mapped.MissingModels.Count > 0 ? mapped.MissingModels : ocrHealth.MissingModels,
            };
        }

        return mapped with
        {
            MissingModels = mapped.MissingModels.Count > 0 ? mapped.MissingModels : exception.MissingModels,
            Limit = mapped.Limit ?? exception.Limit,
            Length = mapped.Length ?? exception.Length,
        };
    }

    /// <summary>服务处于 <see cref="EngineState.Failed"/> 时的浮窗错误：启动超时显示「翻译服务启动超时」（#50），其余提示点「重试」重启服务。</summary>
    /// <param name="failure">失败原因（<see cref="IEngineStatus.Failure"/>），未知时为 <see langword="null"/>。</param>
    public static PopupError EngineFailed(EngineFailure? failure = null) => failure?.Reason == EngineFailureReason.StartupTimeout
        ? new PopupError(PopupErrorKind.EngineStartTimeout)
        : new PopupError(PopupErrorKind.ServiceUnavailable) { Detail = EngineFailedMessage };
}
