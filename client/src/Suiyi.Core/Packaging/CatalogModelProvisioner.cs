using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Packaging;

/// <summary>
/// 按嵌入目录下载预转换翻译模型（Hugging Face 文件）与 OCR（ModelScope）；
/// 开发树若缺目录未覆盖的模型，可回退 <c>scripts/convert_models.py</c>。
/// </summary>
public sealed class CatalogModelProvisioner : IModelProvisioner, IDisposable
{
    private readonly ModelProvisionCatalog _catalog;
    private readonly string _modelsDirectory;
    private readonly string? _repositoryRoot;
    private readonly ProvisionDownloader _downloader;
    private readonly Func<string, string?, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> _runProcess;

    /// <summary>创建实现。</summary>
    /// <param name="modelsDirectory">模型根（绝对路径）。</param>
    /// <param name="catalog">目录；默认嵌入资源。</param>
    /// <param name="repositoryRoot">仓库根（含 <c>scripts/convert_models.py</c>）；便携包为 <see langword="null"/>。</param>
    /// <param name="handler">可选 HTTP handler（单测）。</param>
    /// <param name="runProcess">可选进程启动（单测）；默认 <see cref="Process"/>。</param>
    public CatalogModelProvisioner(
        string modelsDirectory,
        ModelProvisionCatalog? catalog = null,
        string? repositoryRoot = null,
        HttpMessageHandler? handler = null,
        Func<string, string?, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>>? runProcess = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
        _catalog = catalog ?? ModelProvisionCatalog.LoadEmbedded();
        _repositoryRoot = string.IsNullOrWhiteSpace(repositoryRoot) ? null : Path.GetFullPath(repositoryRoot);
        _downloader = new ProvisionDownloader(handler);
        _runProcess = runProcess ?? RunProcessAsync;
    }

    /// <summary>
    /// 解析模型目录：设置里的相对路径相对 <paramref name="baseDirectory"/>；
    /// 未设置时，有仓库根用仓库根 <c>models</c>，否则用 BaseDirectory 下 <c>models</c>。
    /// </summary>
    public static string ResolveModelsDirectory(string? configuredModelsDir, string baseDirectory, string? repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var resolved = EngineCommandResolver.ResolveOptionalPath(configuredModelsDir, baseDirectory);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return Path.GetFullPath(resolved);
        }

        var root = repositoryRoot ?? baseDirectory;
        return Path.GetFullPath(Path.Combine(root, "models"));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListMissingTranslationModels(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        var root = Path.GetFullPath(modelsDirectory);
        return _catalog.DefaultTranslationIds
            .Where(id => !IsTranslationReady(root, id))
            .ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListMissingOcrModels(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        var root = Path.GetFullPath(modelsDirectory);
        return _catalog.RecommendedOcrModels()
            .Where(m => !ProvisionDownloader.IsValidFile(Path.Combine(root, "ocr", m.File), m.Sha256, m.Bytes))
            .Select(m => m.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<ModelProvisionResult> EnsureModelsAsync(
        IReadOnlyList<string> modelIds,
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        if (modelIds.Count == 0)
        {
            return new ModelProvisionResult(true, "未指定翻译模型，已跳过");
        }

        Directory.CreateDirectory(_modelsDirectory);
        var prepared = new List<string>();
        var skipped = new List<string>();
        try
        {
            foreach (var id in modelIds.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsTranslationReady(_modelsDirectory, id))
                {
                    skipped.Add(id);
                    progress?.Report(new ModelProvisionProgress("skip", id, $"已存在：{id}"));
                    continue;
                }

                if (_catalog.TranslationModels.TryGetValue(id, out var entry))
                {
                    await DownloadTranslationAsync(entry, progress, cancellationToken).ConfigureAwait(false);
                    prepared.Add(id);
                    continue;
                }

                if (await TryConvertViaScriptAsync(id, progress, cancellationToken).ConfigureAwait(false))
                {
                    prepared.Add(id);
                    continue;
                }

                return new ModelProvisionResult(
                    false,
                    $"无法准备模型 {id}：目录未收录且本机无可用的 convert_models.py（便携包请用默认中英集，或在开发树安装 engine[convert] 后重试）");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new ModelProvisionResult(false, "准备翻译模型失败：" + ex.Message);
        }

        var parts = new List<string>();
        if (prepared.Count > 0)
        {
            parts.Add("已准备：" + string.Join("、", prepared));
        }

        if (skipped.Count > 0)
        {
            parts.Add("已齐全：" + string.Join("、", skipped));
        }

        return new ModelProvisionResult(true, parts.Count == 0 ? "翻译模型已就绪" : string.Join("；", parts));
    }

    /// <inheritdoc />
    public async Task<ModelProvisionResult> EnsureOcrAsync(
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_modelsDirectory);
        var ocrDir = Path.Combine(_modelsDirectory, "ocr");
        Directory.CreateDirectory(ocrDir);
        var prepared = new List<string>();
        var skipped = new List<string>();
        try
        {
            foreach (var model in _catalog.RecommendedOcrModels())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(ocrDir, model.File);
                if (ProvisionDownloader.IsValidFile(target, model.Sha256, model.Bytes))
                {
                    skipped.Add(model.Id);
                    progress?.Report(new ModelProvisionProgress("skip", model.Id, $"OCR 已存在：{model.Id}"));
                    continue;
                }

                await _downloader.DownloadAsync(
                    model.Url,
                    target,
                    model.Sha256,
                    model.Bytes,
                    model.Id,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                prepared.Add(model.Id);
            }

            WriteOcrMetadata(ocrDir);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new ModelProvisionResult(false, "准备 OCR 模型失败：" + ex.Message);
        }
        var parts = new List<string>();
        if (prepared.Count > 0)
        {
            parts.Add("OCR 已下载：" + string.Join("、", prepared));
        }

        if (skipped.Count > 0)
        {
            parts.Add("OCR 已齐全：" + string.Join("、", skipped));
        }

        return new ModelProvisionResult(true, parts.Count == 0 ? "OCR 已就绪" : string.Join("；", parts));
    }

    /// <inheritdoc />
    public async Task<ModelProvisionResult> EnsureDefaultsAsync(
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var translation = await EnsureModelsAsync(_catalog.DefaultTranslationIds, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!translation.Succeeded)
        {
            return translation;
        }

        if (!_catalog.DefaultOcr)
        {
            return translation;
        }

        var ocr = await EnsureOcrAsync(progress, cancellationToken).ConfigureAwait(false);
        if (!ocr.Succeeded)
        {
            return ocr;
        }

        return new ModelProvisionResult(true, translation.Message + "。" + ocr.Message);
    }

    /// <inheritdoc />
    public void Dispose() => _downloader.Dispose();

    private async Task DownloadTranslationAsync(
        TranslationModelEntry entry,
        IProgress<ModelProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var finalDir = Path.Combine(_modelsDirectory, entry.Id);
        var partialDir = Path.Combine(_modelsDirectory, "." + entry.Id + ".partial");
        if (Directory.Exists(partialDir))
        {
            Directory.Delete(partialDir, recursive: true);
        }

        Directory.CreateDirectory(partialDir);
        try
        {
            foreach (var file in entry.Download.Files)
            {
                var url = string.Create(
                    CultureInfo.InvariantCulture,
                    $"https://huggingface.co/{entry.Download.Repo}/resolve/{entry.Download.Revision}/{file.Name}");
                var dest = Path.Combine(partialDir, file.Name);
                await _downloader.DownloadAsync(url, dest, file.Sha256, file.Bytes, $"{entry.Id}/{file.Name}", progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            WriteSuiyiModelJson(partialDir, entry);
            if (Directory.Exists(finalDir))
            {
                Directory.Delete(finalDir, recursive: true);
            }

            Directory.Move(partialDir, finalDir);
            progress?.Report(new ModelProvisionProgress("done", entry.Id, $"翻译模型就绪：{entry.Id}"));
        }
        catch
        {
            if (Directory.Exists(partialDir))
            {
                Directory.Delete(partialDir, recursive: true);
            }

            throw;
        }
    }

    private static void WriteSuiyiModelJson(string directory, TranslationModelEntry entry)
    {
        JsonObject root;
        if (entry.SuiyiModelTemplate.ValueKind == JsonValueKind.Object)
        {
            root = JsonNode.Parse(entry.SuiyiModelTemplate.GetRawText())!.AsObject();
        }
        else
        {
            root = new JsonObject
            {
                ["id"] = entry.Id,
                ["src"] = entry.Src,
                ["tgt"] = entry.Tgt,
            };
        }

        if (root["quantization"] is null)
        {
            root["quantization"] = "int8";
        }

        root["converted_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        if (root["ctranslate2_version"] is null)
        {
            root["ctranslate2_version"] = "provisioned";
        }
        var files = new JsonObject();
        foreach (var file in entry.Download.Files)
        {
            files[file.Name] = new JsonObject
            {
                ["sha256"] = file.Sha256,
                ["bytes"] = file.Bytes,
            };
        }

        root["files"] = files;
        var path = Path.Combine(directory, "suiyi-model.json");
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", Encoding.UTF8);
    }

    private void WriteOcrMetadata(string ocrDir)
    {
        var files = new JsonObject();
        foreach (var model in _catalog.RecommendedOcrModels())
        {
            var path = Path.Combine(ocrDir, model.File);
            if (!ProvisionDownloader.IsValidFile(path, model.Sha256, model.Bytes))
            {
                continue;
            }

            files[model.File] = new JsonObject
            {
                ["id"] = model.Id,
                ["task"] = model.Task,
                ["ocr_version"] = model.OcrVersion,
                ["sha256"] = model.Sha256,
                ["bytes"] = model.Bytes,
                ["url"] = model.Url,
                ["license"] = model.License,
            };
        }

        var recommended = new JsonObject();
        foreach (var (task, id) in _catalog.OcrRecommended)
        {
            recommended[task] = id;
        }

        recommended["use_cls"] = _catalog.OcrUseCls;
        var meta = new JsonObject
        {
            ["schema_version"] = 1,
            ["rapidocr_requirement"] = _catalog.RapidocrRequirement,
            ["recommended"] = recommended,
            ["updated_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["files"] = files,
        };
        File.WriteAllText(
            Path.Combine(ocrDir, "suiyi-ocr.json"),
            meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            Encoding.UTF8);
    }

    private static bool IsTranslationReady(string modelsDirectory, string modelId)
    {
        var dir = Path.Combine(modelsDirectory, modelId);
        var meta = Path.Combine(dir, "suiyi-model.json");
        var bin = Path.Combine(dir, "model.bin");
        return File.Exists(meta) && File.Exists(bin) && new FileInfo(bin).Length > 0;
    }

    private async Task<bool> TryConvertViaScriptAsync(
        string modelId,
        IProgress<ModelProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_repositoryRoot is null)
        {
            return false;
        }

        var script = Path.Combine(_repositoryRoot, "scripts", "convert_models.py");
        if (!File.Exists(script))
        {
            return false;
        }

        var python = ResolvePython(_repositoryRoot);
        if (python is null)
        {
            return false;
        }

        progress?.Report(new ModelProvisionProgress("convert", modelId, $"正在用 convert_models.py 转换 {modelId}…"));
        var args = new[]
        {
            script,
            "--ids",
            modelId,
            "--out-dir",
            _modelsDirectory,
        };
        var (code, output) = await _runProcess(python, _repositoryRoot, args, cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            progress?.Report(new ModelProvisionProgress("convert", modelId, $"转换失败（退出码 {code}）"));
            return false;
        }

        if (!IsTranslationReady(_modelsDirectory, modelId))
        {
            progress?.Report(new ModelProvisionProgress("convert", modelId, "转换结束但未找到模型目录"));
            return false;
        }

        progress?.Report(new ModelProvisionProgress("done", modelId, $"脚本已转换：{modelId}"));
        _ = output;
        return true;
    }

    private static string? ResolvePython(string repositoryRoot)
    {
        var venv = OperatingSystem.IsWindows()
            ? Path.Combine(repositoryRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(repositoryRoot, ".venv", "bin", "python");
        if (File.Exists(venv))
        {
            return venv;
        }

        return OperatingSystem.IsWindows() ? "python.exe" : "python3";
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 {fileName}");
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, stdout + stderr);
    }
}
