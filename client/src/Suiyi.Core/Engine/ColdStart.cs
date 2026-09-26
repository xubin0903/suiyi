using System.Globalization;

namespace Suiyi.Core.Engine;

/// <summary>
/// 模型空闲卸载后的冷热判断规则（#94，对接引擎 #92/#93，纯函数）。
/// </summary>
/// <remarks>
/// <para>引擎 #93 起，翻译模型连续 <c>model_idle_unload_s</c> 秒（<c>/health</c> 报告，默认 600）没被用到就卸载，
/// 下一次用到时重新加载。引擎按<b>模型</b>记录最后使用时刻（中转语向用两个模型，各自计时），所以客户端也按模型记。</para>
/// <para>规则（<see cref="IsCold"/>）：</para>
/// <list type="bullet">
/// <item><c>model_idle_unload_s</c> 未知（旧引擎 <c>/health</c> 没有该字段、或还没取到 <c>/health</c>）或为 0（不卸载）→ 本规则不起作用，
/// 按老逻辑只看 <c>loaded_models</c>。</item>
/// <item>本客户端从没成功用过这个模型（首次，或服务重启后）→ 冷。</item>
/// <item>距上次成功使用 ≥ <c>model_idle_unload_s − </c><see cref="SafetyMarginSeconds"/>（不小于 0）→ 冷。</item>
/// <item>否则热。</item>
/// </list>
/// <para>留余量是因为：引擎在开始翻译时记时刻，客户端在收到响应时才记，差一个请求耗时；
/// 引擎的整理线程每秒检查一次；请求还可能在服务端排队（翻译锁）。余量宁大勿小：判冷只是多等一会儿，判热却卸载了会误报超时。</para>
/// </remarks>
public static class ColdStartRule
{
    /// <summary>冷热判断的安全余量（秒）：距上次使用超过 <c>model_idle_unload_s − 30</c> 秒就按冷处理。</summary>
    public const int SafetyMarginSeconds = 30;

    /// <summary>判断一个模型是否可能已被空闲卸载（需要按冷启动处理）。</summary>
    /// <param name="sinceLastUse">距本客户端上次成功用到该模型的时间；从没用过时为 <see langword="null"/>。</param>
    /// <param name="idleUnloadSeconds"><c>/health.model_idle_unload_s</c>；未知（旧引擎）时为 <see langword="null"/>。</param>
    /// <returns>本规则判冷时为 <see langword="true"/>；规则不适用（未知或 0）或判热时为 <see langword="false"/>。</returns>
    public static bool IsCold(TimeSpan? sinceLastUse, double? idleUnloadSeconds)
    {
        if (!Applies(idleUnloadSeconds))
        {
            return false;
        }

        if (sinceLastUse is not { } elapsed)
        {
            return true;
        }

        return elapsed >= ColdThreshold(idleUnloadSeconds!.Value);
    }

    /// <summary>本规则是否适用：<c>model_idle_unload_s</c> 已知且大于 0。</summary>
    /// <param name="idleUnloadSeconds"><c>/health.model_idle_unload_s</c>。</param>
    public static bool Applies(double? idleUnloadSeconds) =>
        idleUnloadSeconds is { } idle && double.IsFinite(idle) && idle > 0;

    /// <summary>距上次使用超过多久算冷：<c>max(0, idle − </c><see cref="SafetyMarginSeconds"/><c>)</c>。</summary>
    /// <param name="idleUnloadSeconds"><c>/health.model_idle_unload_s</c>（大于 0）。</param>
    public static TimeSpan ColdThreshold(double idleUnloadSeconds) =>
        TimeSpan.FromSeconds(Math.Max(0, idleUnloadSeconds - SafetyMarginSeconds));
}

/// <summary>
/// 记录本客户端每个翻译模型上次<b>成功</b>用到的时刻（单调时钟），配合 <see cref="ColdStartRule"/> 判断冷热（#94）。线程安全。
/// </summary>
public sealed class ModelUsageTracker
{
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastUsed = new(StringComparer.Ordinal);

    /// <summary>创建。</summary>
    /// <param name="timeProvider">时钟；默认 <see cref="TimeProvider.System"/>。</param>
    public ModelUsageTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>把这些模型记为此刻刚用过（成功响应的 <c>route</c>）。</summary>
    /// <param name="models">模型 id。</param>
    public void MarkUsed(IEnumerable<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var now = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            foreach (var model in models)
            {
                if (!string.IsNullOrEmpty(model))
                {
                    _lastUsed[model] = now;
                }
            }
        }
    }

    /// <summary>清空记录（服务重启、<see cref="EngineClient.Invalidate"/>）：新进程里所有模型都按首次处理。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lastUsed.Clear();
        }
    }

    /// <summary>距上次成功用到该模型的时间；从没用过时为 <see langword="null"/>。</summary>
    /// <param name="model">模型 id。</param>
    public TimeSpan? SinceLastUse(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        long timestamp;
        lock (_gate)
        {
            if (!_lastUsed.TryGetValue(model, out timestamp))
            {
                return null;
            }
        }

        return _timeProvider.GetElapsedTime(timestamp);
    }

    /// <summary>该模型按 <see cref="ColdStartRule.IsCold"/> 是否为冷。</summary>
    /// <param name="model">模型 id。</param>
    /// <param name="idleUnloadSeconds"><c>/health.model_idle_unload_s</c>；未知时为 <see langword="null"/>。</param>
    public bool IsCold(string model, double? idleUnloadSeconds) =>
        ColdStartRule.IsCold(SinceLastUse(model), idleUnloadSeconds);

    /// <summary>
    /// 从已加载模型里去掉可能已被空闲卸载的（冷的），得到「可以当作已加载」的集合，交给 <see cref="TimeoutPolicy"/>。
    /// 规则不适用（<paramref name="idleUnloadSeconds"/> 未知或为 0）时原样返回，即老逻辑。
    /// </summary>
    /// <param name="loadedModels">客户端所知的已加载模型；未知时为 <see langword="null"/>（原样返回）。</param>
    /// <param name="idleUnloadSeconds"><c>/health.model_idle_unload_s</c>。</param>
    public IReadOnlyCollection<string>? FilterWarm(IReadOnlyCollection<string>? loadedModels, double? idleUnloadSeconds)
    {
        if (loadedModels is null || !ColdStartRule.Applies(idleUnloadSeconds))
        {
            return loadedModels;
        }

        return loadedModels.Where(model => !IsCold(model, idleUnloadSeconds)).ToList();
    }

    /// <summary>日志用：列出这些模型的冷热与距上次使用的秒数，如 <c>opus-mt-en-zh=冷(首次)</c>、<c>opus-mt-zh-en=冷(612s)</c>。</summary>
    /// <param name="models">模型 id。</param>
    /// <param name="idleUnloadSeconds"><c>/health.model_idle_unload_s</c>。</param>
    public string Describe(IEnumerable<string> models, double? idleUnloadSeconds)
    {
        ArgumentNullException.ThrowIfNull(models);
        return string.Join(", ", models.Distinct(StringComparer.Ordinal).Select(model =>
        {
            var since = SinceLastUse(model);
            var state = ColdStartRule.IsCold(since, idleUnloadSeconds) ? "冷" : "热";
            var detail = since is { } s
                ? string.Create(CultureInfo.InvariantCulture, $"{(long)s.TotalSeconds}s")
                : "首次";
            return $"{model}={state}({detail})";
        }));
    }
}

/// <summary>
/// 超时后自动重试一次（#94）：第一次按 <c>firstTimeout</c>，超时（<see cref="EngineErrorKind.Timeout"/>）后按冷启动超时再发一次，
/// 仍失败才把错误交给界面。纯逻辑，不依赖 HTTP。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>只重试一次：第二次无论成败都直接返回 / 抛出。</item>
/// <item>只对超时重试。服务不可用、语向缺失等其他错误原样抛出（主流程另有处理）。</item>
/// <item>调用方取消（新请求取代、用户关闭浮窗）抛 <see cref="OperationCanceledException"/>，不重试；
/// 第一次超时与取消同时发生时也按取消处理。</item>
/// </list>
/// </remarks>
public static class TimeoutRetry
{
    /// <summary>最多尝试的次数（首次 + 重试一次）。</summary>
    public const int MaxAttempts = 2;

    /// <summary>执行 <paramref name="attempt"/>，超时后按 <paramref name="retryTimeout"/> 重试一次。</summary>
    /// <typeparam name="T">结果类型。</typeparam>
    /// <param name="attempt">发一次请求：参数为本次超时与调用方令牌。</param>
    /// <param name="firstTimeout">第一次的超时。</param>
    /// <param name="retryTimeout">重试的超时（冷启动超时）。</param>
    /// <param name="onRetry">决定重试时回调（写日志），参数为第一次的超时错误与重试超时；可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    public static async Task<T> ExecuteAsync<T>(
        Func<TimeSpan, CancellationToken, Task<T>> attempt,
        TimeSpan firstTimeout,
        TimeSpan retryTimeout,
        Action<EngineException, TimeSpan>? onRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        try
        {
            return await attempt(firstTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (EngineException ex) when (ex.Kind == EngineErrorKind.Timeout)
        {
            // 超时与调用方取消同时发生时按取消处理，不重试。
            cancellationToken.ThrowIfCancellationRequested();
            onRetry?.Invoke(ex, retryTimeout);
        }

        return await attempt(retryTimeout, cancellationToken).ConfigureAwait(false);
    }
}
