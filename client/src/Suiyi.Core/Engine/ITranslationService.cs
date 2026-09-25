namespace Suiyi.Core.Engine;

/// <summary><see cref="TranslationService"/> 的抽象，便于编排逻辑（#34）单测。</summary>
public interface ITranslationService
{
    /// <summary>翻译一段文本；新调用会取消上一条未完成的调用。</summary>
    /// <exception cref="OperationCanceledException">被取消或被更新的调用取代。</exception>
    /// <exception cref="EngineException">翻译失败。</exception>
    Task<TranslationOutcome> TranslateAsync(string text, string? sourceOverride = null, CancellationToken cancellationToken = default);

    /// <summary>取消当前未完成的调用。</summary>
    void CancelCurrent();
}
