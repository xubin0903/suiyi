using Suiyi.Core.Glossary;

namespace Suiyi.Core.Engine;

// 术语保护（#84，对接 #83 约定）：/translate 的 glossary 字段、/health 的 glossary_* 字段、POST /glossary/reload。

public sealed partial class EngineClient
{
    /// <summary>
    /// 每次 <c>/translate</c> 时读取的术语保护开关（通常取自设置 <c>glossary.enabled</c>），结果写进请求体的 <c>glossary</c> 字段；
    /// 为 <see langword="null"/> 或返回 <see langword="null"/> 时不带该字段，按服务端默认。
    /// </summary>
    public Func<bool?>? GlossaryOverride { get; set; }

    /// <summary>最近一次 <c>/health</c>（或 <c>POST /glossary/reload</c>）报告的术语表状态；未知或服务不支持时为 <see langword="null"/>。</summary>
    public GlossaryStatus? KnownGlossaryStatus
    {
        get
        {
            lock (_gate)
            {
                return _glossary;
            }
        }
    }

    /// <summary>服务是否支持术语保护（<c>/health</c> 有 <c>glossary_*</c> 字段）；还没取过 <c>/health</c> 时为 <see langword="null"/>。</summary>
    public bool? GlossarySupported
    {
        get
        {
            lock (_gate)
            {
                return _glossarySupported;
            }
        }
    }

    /// <summary>
    /// <c>POST /glossary/reload</c>：让服务立即重读用户术语表，返回重读后的状态并更新 <see cref="KnownGlossaryStatus"/>。
    /// 旧版服务没有这个接口（404）时把 <see cref="GlossarySupported"/> 记为 <see langword="false"/> 并返回 <see langword="null"/>。
    /// </summary>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <exception cref="EngineException">服务不可用、超时或其他错误。</exception>
    public async Task<GlossaryStatus?> ReloadGlossaryAsync(CancellationToken cancellationToken = default)
    {
        GlossaryReloadResponse response;
        try
        {
            response = await SendAsync<GlossaryReloadResponse>(
                    HttpMethod.Post,
                    GlossaryContract.ReloadPath,
                    null,
                    TimeSpan.FromMilliseconds(GlossaryReloadTimeoutMs),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EngineException ex) when (ex.StatusCode is 404 or 405)
        {
            lock (_gate)
            {
                _glossary = null;
                _glossarySupported = false;
            }

            return null;
        }

        var status = response.ToGlossaryStatus();
        lock (_gate)
        {
            _glossary = status;
            _glossarySupported = status is not null;
        }

        return status;
    }

    /// <summary><c>POST /glossary/reload</c> 的超时（读 1 MiB 以内的文件）。</summary>
    public const int GlossaryReloadTimeoutMs = 5000;
}
