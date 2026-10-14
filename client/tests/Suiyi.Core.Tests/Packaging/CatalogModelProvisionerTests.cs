using System.Net;
using System.Security.Cryptography;
using System.Text;
using Suiyi.Core.Packaging;

namespace Suiyi.Core.Tests.Packaging;

public sealed class CatalogModelProvisionerTests
{
    [Fact]
    public void LoadEmbedded_HasDefaultZhEnSet()
    {
        var catalog = ModelProvisionCatalog.LoadEmbedded();
        Assert.Contains("opus-mt-zh-en", catalog.DefaultTranslationIds);
        Assert.Contains("opus-mt-en-zh", catalog.DefaultTranslationIds);
        Assert.True(catalog.DefaultOcr);
        Assert.True(catalog.TranslationModels.ContainsKey("opus-mt-zh-en"));
        Assert.NotEmpty(catalog.RecommendedOcrModels());
    }

    [Fact]
    public void ListMissing_WhenEmpty_ReportsDefaults()
    {
        var root = NewTempDir();
        try
        {
            using var provisioner = new CatalogModelProvisioner(root);
            Assert.Equal(["opus-mt-zh-en", "opus-mt-en-zh"], provisioner.ListMissingTranslationModels(root));
            Assert.Equal(3, provisioner.ListMissingOcrModels(root).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ListMissing_WhenPresent_ReturnsEmpty()
    {
        var root = NewTempDir();
        try
        {
            var catalog = CombinedTinyCatalog();
            SeedTranslation(root, "demo-zh-en");
            SeedOcrFromCatalog(root, catalog);

            using var provisioner = new CatalogModelProvisioner(root, catalog);
            Assert.Empty(provisioner.ListMissingTranslationModels(root));
            Assert.Empty(provisioner.ListMissingOcrModels(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureDefaults_AlreadyPresent_SucceedsWithoutDownload()
    {
        var root = NewTempDir();
        try
        {
            var catalog = CombinedTinyCatalog();
            SeedTranslation(root, "demo-zh-en");
            SeedOcrFromCatalog(root, catalog);

            using var handler = new RecordingHandler();
            using var provisioner = new CatalogModelProvisioner(root, catalog, handler: handler);
            var result = await provisioner.EnsureDefaultsAsync();
            Assert.True(result.Succeeded);
            Assert.Contains("已齐全", result.Message, StringComparison.Ordinal);
            Assert.Empty(handler.Urls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureOcr_DownloadsMissingViaHttp()
    {
        var root = NewTempDir();
        try
        {
            var catalog = TinyOcrCatalog();
            var model = catalog.RecommendedOcrModels()[0];
            var payload = Encoding.ASCII.GetBytes("ocr!");
            using var handler = new RecordingHandler();
            handler.PayloadByUrl[model.Url] = payload;
            using var provisioner = new CatalogModelProvisioner(root, catalog, handler: handler);

            Assert.Equal([model.Id], provisioner.ListMissingOcrModels(root));
            var result = await provisioner.EnsureOcrAsync();
            Assert.True(result.Succeeded);
            Assert.Empty(provisioner.ListMissingOcrModels(root));
            Assert.True(File.Exists(Path.Combine(root, "ocr", "suiyi-ocr.json")));
            Assert.Contains(model.Url, handler.Urls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureModels_DownloadsHfFilesAndWritesMetadata()
    {
        var root = NewTempDir();
        try
        {
            var catalog = TinyTranslationCatalog();
            var entry = catalog.TranslationModels["demo-zh-en"];
            var known = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["model.bin"] = Encoding.ASCII.GetBytes("BIN!"),
                ["config.json"] = Encoding.ASCII.GetBytes("{}"),
                ["shared_vocabulary.json"] = Encoding.ASCII.GetBytes("[]"),
                ["source.spm"] = Encoding.ASCII.GetBytes("spm"),
                ["target.spm"] = Encoding.ASCII.GetBytes("spm"),
            };
            var payloads = entry.Download.Files.ToDictionary(
                f => $"https://huggingface.co/{entry.Download.Repo}/resolve/{entry.Download.Revision}/{f.Name}",
                f => known[f.Name],
                StringComparer.Ordinal);
            using var handler = new RecordingHandler();
            foreach (var (url, bytes) in payloads)
            {
                handler.PayloadByUrl[url] = bytes;
            }

            using var provisioner = new CatalogModelProvisioner(root, catalog, handler: handler);

            var result = await provisioner.EnsureModelsAsync(["demo-zh-en"]);
            Assert.True(result.Succeeded);
            Assert.True(File.Exists(Path.Combine(root, "demo-zh-en", "model.bin")));
            Assert.True(File.Exists(Path.Combine(root, "demo-zh-en", "suiyi-model.json")));
            Assert.Empty(provisioner.ListMissingTranslationModels(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureModels_HttpFailure_ReturnsFailed()
    {
        var root = NewTempDir();
        try
        {
            var catalog = TinyTranslationCatalog();
            using var handler = new RecordingHandler { Status = HttpStatusCode.NotFound };
            using var provisioner = new CatalogModelProvisioner(root, catalog, handler: handler);
            var result = await provisioner.EnsureModelsAsync(["demo-zh-en"]);
            Assert.False(result.Succeeded);
            Assert.Contains("失败", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureModels_UnknownId_FallsBackToScript()
    {
        var root = NewTempDir();
        var repo = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "scripts"));
            File.WriteAllText(Path.Combine(repo, "scripts", "convert_models.py"), "# stub\n");
            var ran = false;
            using var provisioner = new CatalogModelProvisioner(
                root,
                TinyTranslationCatalog(),
                repositoryRoot: repo,
                runProcess: (_, _, args, _) =>
                {
                    ran = true;
                    Assert.Contains("--ids", args);
                    Assert.Contains("extra-model", args);
                    SeedTranslation(root, "extra-model");
                    return Task.FromResult((0, "ok"));
                });

            var result = await provisioner.EnsureModelsAsync(["extra-model"]);
            Assert.True(result.Succeeded);
            Assert.True(ran);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureModels_UnknownIdWithoutScript_Fails()
    {
        var root = NewTempDir();
        try
        {
            using var provisioner = new CatalogModelProvisioner(root, TinyTranslationCatalog());
            var result = await provisioner.EnsureModelsAsync(["no-such-model"]);
            Assert.False(result.Succeeded);
            Assert.Contains("无法准备模型", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveModelsDirectory_UsesConfiguredRelativePath()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "suiyi-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var resolved = CatalogModelProvisioner.ResolveModelsDirectory("models", baseDir, repositoryRoot: null);
            Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "models")), resolved);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "suiyi-prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SeedTranslation(string root, string id)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "suiyi-model.json"), """{"id":"x"}""");
        File.WriteAllBytes(Path.Combine(dir, "model.bin"), [1, 2, 3]);
    }


    private static void SeedOcrFromCatalog(string root, ModelProvisionCatalog catalog)
    {
        var ocrDir = Path.Combine(root, "ocr");
        Directory.CreateDirectory(ocrDir);
        foreach (var model in catalog.RecommendedOcrModels())
        {
            // Tests build catalogs so sha256 matches known ASCII payloads.
            var path = Path.Combine(ocrDir, model.File);
            // Re-derive content is not stored; write zeros then fix via catalog builder helpers.
            // For TinyOcrCatalog content is "ocr!".
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("ocr!"));
        }
    }

    private static ModelProvisionCatalog CombinedTinyCatalog()
    {
        // Merge tiny translation + OCR into one catalog JSON.
        var modelBin = Encoding.ASCII.GetBytes("BIN!");
        var config = Encoding.ASCII.GetBytes("{}");
        var vocab = Encoding.ASCII.GetBytes("[]");
        var spm = Encoding.ASCII.GetBytes("spm");
        string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var ocrBytes = Encoding.ASCII.GetBytes("ocr!");
        var ocrSha = Sha(ocrBytes);
        var json = $$"""
        {
          "schema_version": 1,
          "default_translation_ids": ["demo-zh-en"],
          "default_ocr": true,
          "translation_models": [
            {
              "id": "demo-zh-en",
              "src": "zh",
              "tgt": "en",
              "download": {
                "type": "hf-files",
                "repo": "example/demo",
                "revision": "main",
                "files": [
                  { "name": "model.bin", "sha256": "{{Sha(modelBin)}}", "bytes": {{modelBin.Length}} },
                  { "name": "config.json", "sha256": "{{Sha(config)}}", "bytes": {{config.Length}} },
                  { "name": "shared_vocabulary.json", "sha256": "{{Sha(vocab)}}", "bytes": {{vocab.Length}} },
                  { "name": "source.spm", "sha256": "{{Sha(spm)}}", "bytes": {{spm.Length}} },
                  { "name": "target.spm", "sha256": "{{Sha(spm)}}", "bytes": {{spm.Length}} }
                ]
              },
              "suiyi_model_template": { "id": "demo-zh-en", "src": "zh", "tgt": "en" }
            }
          ],
          "ocr": {
            "recommended": { "det": "tiny-det", "cls": "tiny-det", "rec": "tiny-det" },
            "use_cls": false,
            "rapidocr_requirement": "rapidocr>=3.9.2,<3.10",
            "models": [
              {
                "id": "tiny-det",
                "task": "det",
                "ocr_version": "PP-OCRv6",
                "file": "tiny.onnx",
                "bytes": {{ocrBytes.Length}},
                "sha256": "{{ocrSha}}",
                "url": "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/det/tiny.onnx",
                "license": "Apache-2.0"
              }
            ]
          }
        }
        """;
        return ModelProvisionCatalog.Parse(json);
    }

    private static ModelProvisionCatalog TinyOcrCatalog()
    {
        // 4-byte payload with known sha256 of specific content
        var content = Encoding.ASCII.GetBytes("ocr!");
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var json = $$"""
        {
          "schema_version": 1,
          "default_translation_ids": [],
          "default_ocr": true,
          "translation_models": [],
          "ocr": {
            "recommended": { "det": "tiny-det", "cls": "tiny-det", "rec": "tiny-det" },
            "use_cls": false,
            "rapidocr_requirement": "rapidocr>=3.9.2,<3.10",
            "models": [
              {
                "id": "tiny-det",
                "task": "det",
                "ocr_version": "PP-OCRv6",
                "file": "tiny.onnx",
                "bytes": {{content.Length}},
                "sha256": "{{sha}}",
                "url": "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/det/tiny.onnx",
                "license": "Apache-2.0"
              }
            ]
          }
        }
        """;
        return ModelProvisionCatalog.Parse(json);
    }

    private static ModelProvisionCatalog TinyTranslationCatalog()
    {
        var modelBin = Encoding.ASCII.GetBytes("BIN!");
        var config = Encoding.ASCII.GetBytes("{}");
        var vocab = Encoding.ASCII.GetBytes("[]");
        var spm = Encoding.ASCII.GetBytes("spm");
        string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var json = $$"""
        {
          "schema_version": 1,
          "default_translation_ids": ["demo-zh-en"],
          "default_ocr": false,
          "translation_models": [
            {
              "id": "demo-zh-en",
              "src": "zh",
              "tgt": "en",
              "download": {
                "type": "hf-files",
                "repo": "example/demo",
                "revision": "main",
                "files": [
                  { "name": "model.bin", "sha256": "{{Sha(modelBin)}}", "bytes": {{modelBin.Length}} },
                  { "name": "config.json", "sha256": "{{Sha(config)}}", "bytes": {{config.Length}} },
                  { "name": "shared_vocabulary.json", "sha256": "{{Sha(vocab)}}", "bytes": {{vocab.Length}} },
                  { "name": "source.spm", "sha256": "{{Sha(spm)}}", "bytes": {{spm.Length}} },
                  { "name": "target.spm", "sha256": "{{Sha(spm)}}", "bytes": {{spm.Length}} }
                ]
              },
              "suiyi_model_template": { "id": "demo-zh-en", "src": "zh", "tgt": "en", "license": "CC-BY-4.0" }
            }
          ],
          "ocr": { "recommended": {}, "use_cls": false, "rapidocr_requirement": "", "models": [] }
        }
        """;
        return ModelProvisionCatalog.Parse(json);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        public Dictionary<string, byte[]> PayloadByUrl { get; } = new(StringComparer.Ordinal);

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            Urls.Add(url);
            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status));
            }

            if (!PayloadByUrl.TryGetValue(url, out var payload))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
