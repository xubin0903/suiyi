namespace Suiyi.Core.Packaging;

/// <summary>
/// 模型 / OCR 按需下载钩子（M4）。完整下载 UI 与脚本封装留后续 Issue；
/// 目录布局见 <c>docs/engine/模型目录约定.md</c>，便携包约定见 <c>docs/client/M4-便携包.md</c>。
/// </summary>
public interface IModelProvisioner
{
    /// <summary>
    /// 列出 <paramref name="modelsDirectory"/> 中缺失的翻译模型 id（对照引擎清单）。
    /// 本期默认实现不读清单，返回空列表。
    /// </summary>
    /// <param name="modelsDirectory">模型根目录（通常为便携根下的 <c>models</c>）。</param>
    IReadOnlyList<string> ListMissingTranslationModels(string modelsDirectory);

    /// <summary>
    /// 确保指定模型可用（下载 / 转换）。本期默认实现返回「未实现」。
    /// </summary>
    /// <param name="modelIds">模型 id，例如 <c>opus-mt-en-zh</c>。</param>
    /// <param name="cancellationToken">取消。</param>
    Task<ModelProvisionResult> EnsureModelsAsync(IReadOnlyList<string> modelIds, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IModelProvisioner.EnsureModelsAsync"/> 的结果。</summary>
/// <param name="Succeeded">是否已就绪。</param>
/// <param name="Message">给人看的说明（中文）；失败或未实现时说明下一步。</param>
public readonly record struct ModelProvisionResult(bool Succeeded, string Message)
{
    /// <summary>本期占位：尚未实现按需下载。</summary>
    public static ModelProvisionResult NotImplemented { get; } = new(
        false,
        "按需下载尚未实现：请按 docs/engine/模型目录约定.md 用 scripts/convert_models.py 与 scripts/download_ocr_models.py 准备模型，或等待后续 Issue。");
}

/// <summary>默认占位实现：不下载、不扫描，仅返回说明。</summary>
public sealed class StubModelProvisioner : IModelProvisioner
{
    /// <inheritdoc />
    public IReadOnlyList<string> ListMissingTranslationModels(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        return [];
    }

    /// <inheritdoc />
    public Task<ModelProvisionResult> EnsureModelsAsync(IReadOnlyList<string> modelIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ModelProvisionResult.NotImplemented);
    }
}
