namespace Suiyi.Core.Engine;

/// <summary>框选翻译入口：上传截图，一次往返拿到原文与译文（主流程串接见 #58）。</summary>
public interface IOcrTranslationService
{
    /// <summary>识别并翻译一张 PNG 截图。</summary>
    /// <param name="png">PNG 字节。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <exception cref="OperationCanceledException">调用方取消，或被更新的调用取代。</exception>
    /// <exception cref="EngineException">识别或翻译失败（含客户端预检拦截的 <see cref="EngineErrorKind.ImageTooLarge"/>）。</exception>
    Task<OcrTranslationOutcome> TranslateImageAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken = default);

    /// <summary>取消当前未完成的调用（如用户关闭浮窗、重新框选）。</summary>
    void CancelCurrent();
}
