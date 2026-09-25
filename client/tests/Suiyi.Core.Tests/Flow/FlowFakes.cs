using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Flow;

/// <summary>可控的翻译服务：每次调用挂起，由测试决定结果；取消时抛出 <see cref="OperationCanceledException"/>。</summary>
internal sealed class FakeTranslationService : ITranslationService
{
    private readonly List<Call> _calls = [];

    public IReadOnlyList<Call> Calls => _calls;

    public int CancelCurrentCount { get; private set; }

    /// <summary>模拟取消没能生效（请求已发出、服务照常返回）：取消令牌不再让任务结束。</summary>
    public bool IgnoreCancellation { get; set; }

    public Task<TranslationOutcome> TranslateAsync(string text, string? sourceOverride = null, CancellationToken cancellationToken = default)
    {
        // 同步延续：Complete / Fail 返回时编排器已处理完毕，测试无需等待。
        var call = new Call(text, sourceOverride, new TaskCompletionSource<TranslationOutcome>(), cancellationToken);
        if (!IgnoreCancellation)
        {
            cancellationToken.Register(() => call.Completion.TrySetCanceled(cancellationToken));
        }

        _calls.Add(call);
        return call.Completion.Task;
    }

    public void CancelCurrent() => CancelCurrentCount++;

    public void Complete(int index, string translation = "你好", string source = "en", string target = "zh") =>
        Inline(() => _calls[index].Completion.SetResult(Outcome(translation, source, target)));

    public void Fail(int index, Exception exception) => Inline(() => _calls[index].Completion.SetException(exception));

    /// <summary>
    /// 在没有同步上下文的情况下完成任务：xUnit 的同步上下文会阻止 <c>ConfigureAwait(false)</c> 的延续内联执行，
    /// 清掉后延续在 SetResult 返回前跑完，测试无需等待。
    /// </summary>
    public static void Inline(Action complete)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            complete();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    public static TranslationOutcome Outcome(string translation = "你好", string source = "en", string target = "zh") => new()
    {
        Text = translation,
        SourceLanguage = source,
        SourceDetected = true,
        TargetLanguage = target,
        Retargeted = false,
        Route = [$"opus-mt-{source}-{target}"],
        ServerElapsedMs = 42,
        ClientElapsed = TimeSpan.FromMilliseconds(60),
        RequestCount = 1,
    };

    internal sealed record Call(string Text, string? SourceOverride, TaskCompletionSource<TranslationOutcome> Completion, CancellationToken Token);
}

/// <summary>可控的服务状态。</summary>
internal sealed class FakeEngineStatus : IEngineStatus
{
    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    public EngineState State { get; set; } = EngineState.Ready;

    public EngineFailure? Failure { get; set; }

    public int HealthChecks { get; private set; }

    /// <summary>健康检查被请求时执行（模拟监管器察觉到进程已退出）。</summary>
    public Action? OnHealthCheck { get; set; }

    public void RequestHealthCheck()
    {
        HealthChecks++;
        OnHealthCheck?.Invoke();
    }

    public int Restarts { get; private set; }

    /// <summary>重启被请求时执行（模拟监管器进入 Starting）。</summary>
    public Action? OnRestart { get; set; }

    public Task RestartAsync()
    {
        Restarts++;
        OnRestart?.Invoke();
        return Task.CompletedTask;
    }

    public void Raise(EngineState state, EngineFailure? failure = null)
    {
        State = state;
        Failure = failure;
        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(state, EngineOwnership.Managed, state.ToString(), failure));
    }
}
