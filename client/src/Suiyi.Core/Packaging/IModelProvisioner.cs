namespace Suiyi.Core.Packaging;

/// <summary>
/// 模型 / OCR 按需准备（M4 #135）。目录布局见 <c>docs/engine/模型目录约定.md</c>，
/// 便携约定见 <c>docs/client/M4-便携包.md</c>。权重不打进 zip。
/// </summary>
public interface IModelProvisioner
{
    /// <summary>
    /// 列出默认中英翻译模型中，在 <paramref name="modelsDirectory"/> 仍缺失的 id。
    /// </summary>
    /// <param name="modelsDirectory">模型根目录（通常为便携根下的 <c>models</c>）。</param>
    IReadOnlyList<string> ListMissingTranslationModels(string modelsDirectory);

    /// <summary>
    /// 列出推荐 OCR 组合中，在 <paramref name="modelsDirectory"/>/ocr 仍缺失或校验失败的 id。
    /// </summary>
    /// <param name="modelsDirectory">模型根目录。</param>
    IReadOnlyList<string> ListMissingOcrModels(string modelsDirectory);

    /// <summary>
    /// 确保指定翻译模型可用（下载预转换产物；开发树可回退 convert 脚本）。
    /// </summary>
    /// <param name="modelIds">模型 id，例如 <c>opus-mt-en-zh</c>。</param>
    /// <param name="progress">可选进度。</param>
    /// <param name="cancellationToken">取消。</param>
    Task<ModelProvisionResult> EnsureModelsAsync(
        IReadOnlyList<string> modelIds,
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>确保推荐 OCR 模型可用（从 ModelScope 下载并校验）。</summary>
    /// <param name="progress">可选进度。</param>
    /// <param name="cancellationToken">取消。</param>
    Task<ModelProvisionResult> EnsureOcrAsync(
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>准备目录中的默认集：中英翻译 + 推荐 OCR。</summary>
    /// <param name="progress">可选进度。</param>
    /// <param name="cancellationToken">取消。</param>
    Task<ModelProvisionResult> EnsureDefaultsAsync(
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>按需准备过程中的进度。</summary>
/// <param name="Phase">阶段短名，例如 <c>download</c> / <c>verify</c> / <c>convert</c>。</param>
/// <param name="ItemId">当前模型或文件 id。</param>
/// <param name="Message">给人看的中文说明。</param>
/// <param name="BytesReceived">已接收字节；未知时为 <see langword="null"/>。</param>
/// <param name="TotalBytes">总字节；未知时为 <see langword="null"/>。</param>
public readonly record struct ModelProvisionProgress(
    string Phase,
    string ItemId,
    string Message,
    long? BytesReceived = null,
    long? TotalBytes = null);

/// <summary><see cref="IModelProvisioner"/> 的结果。</summary>
/// <param name="Succeeded">是否已就绪（或本就齐全）。</param>
/// <param name="Message">给人看的说明（中文）。</param>
public readonly record struct ModelProvisionResult(bool Succeeded, string Message)
{
    /// <summary>本期占位：尚未实现按需下载（仅 <see cref="StubModelProvisioner"/>）。</summary>
    public static ModelProvisionResult NotImplemented { get; } = new(
        false,
        "按需下载尚未实现：请按 docs/engine/模型目录约定.md 用 scripts/convert_models.py 与 scripts/download_ocr_models.py 准备模型，或使用 CatalogModelProvisioner。");
}

/// <summary>默认占位实现：不下载、不扫描，仅返回说明。测试或显式切换时可用。</summary>
public sealed class StubModelProvisioner : IModelProvisioner
{
    /// <inheritdoc />
    public IReadOnlyList<string> ListMissingTranslationModels(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListMissingOcrModels(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        return [];
    }

    /// <inheritdoc />
    public Task<ModelProvisionResult> EnsureModelsAsync(
        IReadOnlyList<string> modelIds,
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ModelProvisionResult.NotImplemented);
    }

    /// <inheritdoc />
    public Task<ModelProvisionResult> EnsureOcrAsync(
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ModelProvisionResult.NotImplemented);
    }

    /// <inheritdoc />
    public Task<ModelProvisionResult> EnsureDefaultsAsync(
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ModelProvisionResult.NotImplemented);
    }
}
