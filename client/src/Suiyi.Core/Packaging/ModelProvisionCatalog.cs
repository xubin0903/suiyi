using System.Text.Json;

namespace Suiyi.Core.Packaging;

/// <summary>嵌入的按需下载目录（翻译 + OCR）。</summary>
public sealed class ModelProvisionCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>从嵌入资源加载正式目录。</summary>
    public static ModelProvisionCatalog LoadEmbedded()
    {
        var assembly = typeof(ModelProvisionCatalog).Assembly;
        const string ResourceName = "Suiyi.Core.Packaging.model_provision_catalog.json";
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"缺少嵌入资源 {ResourceName}");
        return Load(stream);
    }

    /// <summary>从 JSON 流加载。</summary>
    public static ModelProvisionCatalog Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var dto = JsonSerializer.Deserialize<CatalogDto>(stream, JsonOptions)
            ?? throw new InvalidOperationException("模型准备目录为空");
        return FromDto(dto);
    }

    /// <summary>从 JSON 文本加载（测试用）。</summary>
    public static ModelProvisionCatalog Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var dto = JsonSerializer.Deserialize<CatalogDto>(json, JsonOptions)
            ?? throw new InvalidOperationException("模型准备目录为空");
        return FromDto(dto);
    }

    private static ModelProvisionCatalog FromDto(CatalogDto dto)
    {
        if (dto.SchemaVersion != 1)
        {
            throw new InvalidOperationException($"不支持的模型准备目录 schema_version：{dto.SchemaVersion}");
        }

        var translation = (dto.TranslationModels ?? [])
            .Select(TranslationModelEntry.FromDto)
            .ToDictionary(m => m.Id, StringComparer.Ordinal);
        var ocr = (dto.Ocr?.Models ?? [])
            .Select(OcrModelEntry.FromDto)
            .ToDictionary(m => m.Id, StringComparer.Ordinal);
        return new ModelProvisionCatalog
        {
            DefaultTranslationIds = dto.DefaultTranslationIds ?? [],
            DefaultOcr = dto.DefaultOcr,
            TranslationModels = translation,
            OcrModels = ocr,
            OcrRecommended = dto.Ocr?.Recommended ?? new Dictionary<string, string>(),
            OcrUseCls = dto.Ocr?.UseCls ?? false,
            RapidocrRequirement = dto.Ocr?.RapidocrRequirement ?? string.Empty,
        };
    }

    /// <summary>默认要准备的翻译模型 id。</summary>
    public required IReadOnlyList<string> DefaultTranslationIds { get; init; }

    /// <summary>默认是否一并准备 OCR。</summary>
    public bool DefaultOcr { get; init; }

    /// <summary>翻译模型条目。</summary>
    public required IReadOnlyDictionary<string, TranslationModelEntry> TranslationModels { get; init; }

    /// <summary>OCR 模型条目。</summary>
    public required IReadOnlyDictionary<string, OcrModelEntry> OcrModels { get; init; }

    /// <summary>推荐 OCR 组合（det/rec/cls → id）。</summary>
    public required IReadOnlyDictionary<string, string> OcrRecommended { get; init; }

    /// <summary>是否启用方向分类。</summary>
    public bool OcrUseCls { get; init; }

    /// <summary>RapidOCR 版本约束说明。</summary>
    public string RapidocrRequirement { get; init; } = string.Empty;

    /// <summary>推荐 OCR 模型列表（按 det/cls/rec）。</summary>
    public IReadOnlyList<OcrModelEntry> RecommendedOcrModels()
    {
        var order = new[] { "det", "cls", "rec" };
        var list = new List<OcrModelEntry>();
        foreach (var task in order)
        {
            if (OcrRecommended.TryGetValue(task, out var id) && OcrModels.TryGetValue(id, out var model))
            {
                list.Add(model);
            }
        }

        return list;
    }

    private sealed class CatalogDto
    {
        public int SchemaVersion { get; set; }

        public List<string>? DefaultTranslationIds { get; set; }

        public bool DefaultOcr { get; set; }

        public List<TranslationModelDto>? TranslationModels { get; set; }

        public OcrSectionDto? Ocr { get; set; }
    }

    private sealed class OcrSectionDto
    {
        public Dictionary<string, string>? Recommended { get; set; }

        public bool UseCls { get; set; }

        public string? RapidocrRequirement { get; set; }

        public List<OcrModelDto>? Models { get; set; }
    }

    internal sealed class TranslationModelDto
    {
        public string? Id { get; set; }

        public string? Src { get; set; }

        public string? Tgt { get; set; }

        public string? Tier { get; set; }

        public string? License { get; set; }

        public string? Attribution { get; set; }

        public string? HfRepo { get; set; }

        public string? HfRevision { get; set; }

        public string? SrcPrefixToken { get; set; }

        public string? Quantization { get; set; }

        public string? Architecture { get; set; }

        public DownloadDto? Download { get; set; }

        public JsonElement? SuiyiModelTemplate { get; set; }
    }

    internal sealed class DownloadDto
    {
        public string? Type { get; set; }

        public string? Repo { get; set; }

        public string? Revision { get; set; }

        public List<FileDto>? Files { get; set; }
    }

    internal sealed class FileDto
    {
        public string? Name { get; set; }

        public string? Sha256 { get; set; }

        public long Bytes { get; set; }
    }

    internal sealed class OcrModelDto
    {
        public string? Id { get; set; }

        public string? Task { get; set; }

        public string? OcrVersion { get; set; }

        public string? File { get; set; }

        public long Bytes { get; set; }

        public string? Sha256 { get; set; }

        public string? Url { get; set; }

        public string? License { get; set; }

        public string? Tier { get; set; }
    }
}

/// <summary>翻译模型目录条目。</summary>
public sealed class TranslationModelEntry
{
    /// <summary>模型 id。</summary>
    public required string Id { get; init; }

    /// <summary>源语种。</summary>
    public required string Src { get; init; }

    /// <summary>目标语种。</summary>
    public required string Tgt { get; init; }

    /// <summary>下载方式。</summary>
    public required HfFilesDownload Download { get; init; }

    /// <summary>写入 <c>suiyi-model.json</c> 的模板字段（JSON 对象）。</summary>
    public required JsonElement SuiyiModelTemplate { get; init; }

    internal static TranslationModelEntry FromDto(ModelProvisionCatalog.TranslationModelDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            throw new InvalidOperationException("翻译模型缺少 id");
        }

        if (dto.Download is null || !string.Equals(dto.Download.Type, "hf-files", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{dto.Id}：目前只支持 download.type=hf-files");
        }

        if (string.IsNullOrWhiteSpace(dto.Download.Repo))
        {
            throw new InvalidOperationException($"{dto.Id}：缺少 download.repo");
        }

        var files = (dto.Download.Files ?? [])
            .Select(f =>
            {
                if (string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.Sha256) || f.Bytes <= 0)
                {
                    throw new InvalidOperationException($"{dto.Id}：文件条目不完整");
                }

                return new ProvisionFile(f.Name, f.Sha256.ToLowerInvariant(), f.Bytes);
            })
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException($"{dto.Id}：download.files 为空");
        }

        return new TranslationModelEntry
        {
            Id = dto.Id,
            Src = dto.Src ?? string.Empty,
            Tgt = dto.Tgt ?? string.Empty,
            Download = new HfFilesDownload(dto.Download.Repo, dto.Download.Revision ?? "main", files),
            SuiyiModelTemplate = dto.SuiyiModelTemplate ?? default,
        };
    }
}

/// <summary>Hugging Face 按文件下载。</summary>
/// <param name="Repo">仓库，如 <c>manancode/opus-mt-zh-en-ctranslate2-android</c>。</param>
/// <param name="Revision">修订（commit / 分支 / 标签）。</param>
/// <param name="Files">要下载并校验的文件。</param>
public sealed record HfFilesDownload(string Repo, string Revision, IReadOnlyList<ProvisionFile> Files);

/// <summary>OCR 目录条目。</summary>
public sealed class OcrModelEntry
{
    /// <summary>模型 id。</summary>
    public required string Id { get; init; }

    /// <summary>任务：det / cls / rec。</summary>
    public required string Task { get; init; }

    /// <summary>OCR 版本标签。</summary>
    public required string OcrVersion { get; init; }

    /// <summary>文件名。</summary>
    public required string File { get; init; }

    /// <summary>期望字节数。</summary>
    public long Bytes { get; init; }

    /// <summary>期望 sha256（小写）。</summary>
    public required string Sha256 { get; init; }

    /// <summary>下载 URL（ModelScope）。</summary>
    public required string Url { get; init; }

    /// <summary>许可证。</summary>
    public required string License { get; init; }

    internal static OcrModelEntry FromDto(ModelProvisionCatalog.OcrModelDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (string.IsNullOrWhiteSpace(dto.Id)
            || string.IsNullOrWhiteSpace(dto.File)
            || string.IsNullOrWhiteSpace(dto.Sha256)
            || string.IsNullOrWhiteSpace(dto.Url)
            || dto.Bytes <= 0)
        {
            throw new InvalidOperationException("OCR 模型条目不完整");
        }

        return new OcrModelEntry
        {
            Id = dto.Id,
            Task = dto.Task ?? string.Empty,
            OcrVersion = dto.OcrVersion ?? string.Empty,
            File = dto.File,
            Bytes = dto.Bytes,
            Sha256 = dto.Sha256.ToLowerInvariant(),
            Url = dto.Url,
            License = dto.License ?? string.Empty,
        };
    }
}

/// <summary>待下载文件。</summary>
/// <param name="Name">文件名（无路径）。</param>
/// <param name="Sha256">小写 sha256。</param>
/// <param name="Bytes">字节数。</param>
public sealed record ProvisionFile(string Name, string Sha256, long Bytes);
