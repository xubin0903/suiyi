using Suiyi.Core.Engine;
using Suiyi.Core.Popup;

namespace Suiyi.Core.Flow;

/// <summary><see cref="EngineException"/> → 浮窗错误（纯函数）。</summary>
public static class PopupErrorMapper
{
    /// <summary>翻译服务启动失败时的浮窗提示。</summary>
    public const string EngineFailedMessage = "翻译服务启动失败，点「重试」会重启翻译服务";

    /// <summary>映射引擎错误。</summary>
    public static PopupError Map(EngineException exception)
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
                OcrResultMapper.MapError(exception.ErrorCode, exception.Details),
            _ => new PopupError(PopupErrorKind.Other) { Detail = exception.UserMessage },
        };
    }

    /// <summary>服务处于 <see cref="EngineState.Failed"/> 时的浮窗错误：启动超时显示「翻译服务启动超时」（#50），其余提示点「重试」重启服务。</summary>
    /// <param name="failure">失败原因（<see cref="IEngineStatus.Failure"/>），未知时为 <see langword="null"/>。</param>
    public static PopupError EngineFailed(EngineFailure? failure = null) => failure?.Reason == EngineFailureReason.StartupTimeout
        ? new PopupError(PopupErrorKind.EngineStartTimeout)
        : new PopupError(PopupErrorKind.ServiceUnavailable) { Detail = EngineFailedMessage };
}
