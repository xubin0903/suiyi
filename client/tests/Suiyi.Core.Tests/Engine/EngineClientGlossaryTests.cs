using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Engine;
using Suiyi.Core.Glossary;

namespace Suiyi.Core.Tests.Engine;

/// <summary>术语保护（#84）与服务的对接：请求字段 glossary、/health 的 glossary_* 字段、POST /glossary/reload（#83 约定）。</summary>
public sealed class EngineClientGlossaryTests : IDisposable
{
    private const string GlossaryHealth = """
        {"status":"ok","version":"0.0.2","models_dir":"/m","uptime_s":1.0,"loaded_models":[],
         "glossary_enabled":true,"glossary_builtin_entries":312,"glossary_user_path":"C:\\Users\\a\\AppData\\Roaming\\suiyi\\glossary.tsv",
         "glossary_user_entries":3,"glossary_error":null,"glossary_warnings":["第 4 行：缺少目标词"]}
        """;

    private const string ReloadBody = """
        {"glossary_enabled":false,"glossary_builtin_entries":312,"glossary_user_path":null,
         "glossary_user_entries":0,"glossary_error":"文件不是有效的 UTF-8","glossary_warnings":[]}
        """;

    private readonly FakeEngineHandler _handler = new();
    private readonly EngineClient _client;

    public EngineClientGlossaryTests()
    {
        _client = new EngineClient(EngineClient.DefaultPort, _handler, new FakeTimeProvider());
    }

    public void Dispose() => _client.Dispose();

    private static JsonElement Body(RecordedRequest request) => JsonDocument.Parse(request.Body!).RootElement;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Translate_SendsGlossaryFieldFromOverride(bool enabled)
    {
        _client.GlossaryOverride = () => enabled;

        await _client.TranslateAsync("Kubernetes is a container orchestration engine.", "en", "zh");

        var body = Body(Assert.Single(_handler.TranslateRequests));
        Assert.Equal(enabled, body.GetProperty(GlossaryContract.RequestField).GetBoolean());
    }

    [Fact]
    public async Task Translate_OverrideReadEachRequest()
    {
        var enabled = true;
        _client.GlossaryOverride = () => enabled;

        await _client.TranslateAsync("a", "en", "zh");
        enabled = false;
        await _client.TranslateAsync("b", "en", "zh");

        Assert.Equal([true, false], _handler.TranslateRequests.Select(r => Body(r).GetProperty("glossary").GetBoolean()));
    }

    [Fact]
    public async Task Translate_NoOverride_OmitsField()
    {
        await _client.TranslateAsync("a", "en", "zh");
        _client.GlossaryOverride = () => null;
        await _client.TranslateAsync("b", "en", "zh");

        Assert.All(_handler.TranslateRequests, r => Assert.False(Body(r).TryGetProperty("glossary", out _)));
    }

    [Fact]
    public async Task OcrTranslate_DoesNotSendGlossary()
    {
        _client.GlossaryOverride = () => false;

        await _client.OcrTranslateAsync(TestPng.Small, "auto", "zh", "en");

        var request = Assert.Single(_handler.OcrRequests);
        Assert.DoesNotContain("glossary", request.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_ParsesGlossaryFields()
    {
        _handler.Health = GlossaryHealth;

        var health = await _client.GetHealthAsync();

        Assert.True(health.GlossaryEnabled);
        var status = _client.KnownGlossaryStatus;
        Assert.NotNull(status);
        Assert.Equal(
            new GlossaryStatus(true, 312, 3, @"C:\Users\a\AppData\Roaming\suiyi\glossary.tsv", null, ["第 4 行：缺少目标词"]),
            status);
        Assert.True(_client.GlossarySupported);
        Assert.Equal(status, health.ToGlossaryStatus());
    }

    [Fact]
    public async Task Health_OldEngine_StatusNullAndUnsupported()
    {
        Assert.Null(_client.KnownGlossaryStatus);
        Assert.Null(_client.GlossarySupported);

        await _client.GetHealthAsync();

        Assert.Null(_client.KnownGlossaryStatus);
        Assert.False(_client.GlossarySupported);
    }

    [Fact]
    public async Task Health_MissingWarnings_IsEmptyList()
    {
        _handler.Health = """{"status":"ok","version":"x","models_dir":"/m","uptime_s":1,"loaded_models":[],"glossary_enabled":false,"glossary_builtin_entries":10}""";

        await _client.GetHealthAsync();

        var status = _client.KnownGlossaryStatus!;
        Assert.False(status.ServerEnabled);
        Assert.Empty(status.Warnings);
        Assert.Null(status.UserPath);
        Assert.False(status.HasError);
    }

    [Fact]
    public async Task Invalidate_ClearsGlossaryStatus()
    {
        _handler.Health = GlossaryHealth;
        await _client.GetHealthAsync();

        _client.Invalidate();

        Assert.Null(_client.KnownGlossaryStatus);
        Assert.Null(_client.GlossarySupported);
    }

    [Fact]
    public async Task Reload_PostsAndReturnsStatus()
    {
        _handler.GlossaryReload = _ => FakeEngineHandler.Json(HttpStatusCode.OK, ReloadBody);

        var status = await _client.ReloadGlossaryAsync();

        var request = Assert.Single(_handler.Requests, r => r.Path == "/glossary/reload");
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.NotNull(status);
        Assert.Equal("文件不是有效的 UTF-8", status.Error);
        Assert.True(status.HasError);
        Assert.False(status.ServerEnabled);
        Assert.Equal(status, _client.KnownGlossaryStatus);
        Assert.True(_client.GlossarySupported);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task Reload_OldEngine_ReturnsNullAndUnsupported(HttpStatusCode code)
    {
        _handler.Health = GlossaryHealth;
        await _client.GetHealthAsync();
        _handler.GlossaryReload = _ => FakeEngineHandler.Json(code, """{"detail":"Not Found"}""");

        Assert.Null(await _client.ReloadGlossaryAsync());
        Assert.Null(_client.KnownGlossaryStatus);
        Assert.False(_client.GlossarySupported);
    }

    [Fact]
    public async Task Reload_ServerError_Throws()
    {
        _handler.GlossaryReload = _ => FakeEngineHandler.Json(HttpStatusCode.InternalServerError, """{"detail":"boom"}""");

        await Assert.ThrowsAsync<EngineException>(() => _client.ReloadGlossaryAsync());
    }
}
