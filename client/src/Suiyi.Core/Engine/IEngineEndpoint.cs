namespace Suiyi.Core.Engine;

/// <summary>监管器对服务 HTTP 接口的最小需求，默认实现 <see cref="EngineClientEndpoint"/>。</summary>
public interface IEngineEndpoint
{
    /// <summary><c>GET /health</c> 返回 <c>status=ok</c> 时为 <see langword="true"/>；连不上、超时、响应异常都为 <see langword="false"/>，不抛异常。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);

    /// <summary>服务（重新）就绪时调用：清理旧缓存，并发一条短句预热、丢弃结果。失败不抛异常。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task WarmUpAsync(CancellationToken cancellationToken);
}

/// <summary>基于 #28 <see cref="EngineClient"/> 的 <see cref="IEngineEndpoint"/>。</summary>
public sealed class EngineClientEndpoint : IEngineEndpoint
{
    /// <summary>预热用的短句（zh→en）。</summary>
    public const string WarmUpText = "你好";

    private readonly EngineClient _client;

    /// <summary>创建。</summary>
    /// <param name="client">引擎客户端（不随本对象释放）。</param>
    public EngineClientEndpoint(EngineClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var health = await _client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(health.Status, "ok", StringComparison.Ordinal);
        }
        catch (EngineException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        // 新进程的已加载模型、语向可能与旧缓存不同。
        _client.Invalidate();
        try
        {
            await _client.TranslateAsync(WarmUpText, "zh", "en", cancellationToken).ConfigureAwait(false);
        }
        catch (EngineException)
        {
            // 预热失败不影响状态（例如没装 zh→en 模型）。
        }
    }
}
