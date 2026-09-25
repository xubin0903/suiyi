using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

public sealed class EngineClientEndpointTests : IDisposable
{
    private readonly FakeEngineHandler _handler = new();
    private readonly EngineClient _client;
    private readonly EngineClientEndpoint _endpoint;

    public EngineClientEndpointTests()
    {
        _client = new EngineClient(EngineClient.DefaultPort, _handler);
        _endpoint = new EngineClientEndpoint(_client);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task IsHealthy_StatusOk_True()
    {
        Assert.True(await _endpoint.IsHealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IsHealthy_StatusNotOk_False()
    {
        _handler.Health = """{"status":"starting","loaded_models":[]}""";

        Assert.False(await _endpoint.IsHealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IsHealthy_ConnectionRefused_False()
    {
        using var client = new EngineClient(EngineClient.DefaultPort, new ThrowingHandler());

        Assert.False(await new EngineClientEndpoint(client).IsHealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IsHealthy_OtherProgramOnPort_False()
    {
        using var client = new EngineClient(EngineClient.DefaultPort, new StaticHandler(HttpStatusCode.OK, "<html>not suiyi</html>"));

        Assert.False(await new EngineClientEndpoint(client).IsHealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WarmUp_InvalidatesCacheAndTranslatesZhToEn()
    {
        await _client.GetLanguagesAsync();

        await _endpoint.WarmUpAsync(CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count(r => r.Path == "/languages"));
        using var doc = JsonDocument.Parse(Assert.Single(_handler.TranslateRequests).Body!);
        Assert.Equal("zh", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("en", doc.RootElement.GetProperty("target").GetString());
    }

    [Fact]
    public async Task WarmUp_EngineError_Swallowed()
    {
        _handler.Translate = (_, _) => Task.FromResult(FakeEngineHandler.Json(422, """
            {"error":{"code":"unsupported_pair","message":"","details":{"missing_models":["opus-mt-zh-en"]}}}
            """));

        await _endpoint.WarmUpAsync(CancellationToken.None);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused));
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
