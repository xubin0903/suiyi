using Microsoft.Extensions.Time.Testing;
using Suiyi.Core.Engine;

namespace Suiyi.Core.Tests.Engine;

/// <summary>#94 冷热判断规则、模型使用记录与超时重试（纯逻辑）。</summary>
public sealed class ColdStartRuleTests
{
    // ---- ColdStartRule：冷热判断 ----

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(3600.0)]
    public void IsCold_OldEngineWithoutField_NeverColdByThisRule(double? sinceSeconds)
    {
        // 旧引擎（/health 没有 model_idle_unload_s）：本规则不起作用，按老逻辑只看 loaded_models。
        var since = sinceSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        Assert.False(ColdStartRule.IsCold(since, idleUnloadSeconds: null));
        Assert.False(ColdStartRule.Applies(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(99999.0)]
    public void IsCold_IdleUnloadDisabled_NeverCold(double? sinceSeconds)
    {
        var since = sinceSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        Assert.False(ColdStartRule.IsCold(since, idleUnloadSeconds: 0));
        Assert.False(ColdStartRule.Applies(0));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void IsCold_InvalidIdleValue_TreatedAsUnknown(double idle)
    {
        Assert.False(ColdStartRule.Applies(idle));
        Assert.False(ColdStartRule.IsCold(null, idle));
    }

    [Fact]
    public void IsCold_NeverUsed_IsCold()
    {
        Assert.True(ColdStartRule.IsCold(null, 600));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(10, false)]
    [InlineData(569, false)]
    [InlineData(570, true)] // 600 − 30 s 余量
    [InlineData(600, true)]
    [InlineData(3600, true)]
    public void IsCold_ComparesElapsedWithIdleMinusMargin(int sinceSeconds, bool cold)
    {
        Assert.Equal(cold, ColdStartRule.IsCold(TimeSpan.FromSeconds(sinceSeconds), 600));
    }

    [Fact]
    public void IsCold_JustBelowThreshold_IsWarm()
    {
        Assert.False(ColdStartRule.IsCold(TimeSpan.FromSeconds(570) - TimeSpan.FromTicks(1), 600));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(30)]
    public void IsCold_IdleNotLargerThanMargin_AlwaysCold(double idle)
    {
        Assert.Equal(TimeSpan.Zero, ColdStartRule.ColdThreshold(idle));
        Assert.True(ColdStartRule.IsCold(TimeSpan.Zero, idle));
    }

    // ---- ModelUsageTracker ----

    [Fact]
    public void Tracker_RecordsPerModelAndMeasuresElapsed()
    {
        var time = new FakeTimeProvider();
        var tracker = new ModelUsageTracker(time);
        Assert.Null(tracker.SinceLastUse("opus-mt-en-zh"));

        tracker.MarkUsed(["opus-mt-en-zh"]);
        time.Advance(TimeSpan.FromSeconds(100));
        tracker.MarkUsed(["opus-mt-zh-en"]);
        time.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(120), tracker.SinceLastUse("opus-mt-en-zh"));
        Assert.Equal(TimeSpan.FromSeconds(20), tracker.SinceLastUse("opus-mt-zh-en"));
        Assert.Null(tracker.SinceLastUse("opus-mt-ja-en"));
    }

    [Fact]
    public void Tracker_FilterWarm_DropsColdModelsOnly()
    {
        var time = new FakeTimeProvider();
        var tracker = new ModelUsageTracker(time);
        tracker.MarkUsed(["a"]);
        time.Advance(TimeSpan.FromSeconds(600));
        tracker.MarkUsed(["b"]);
        time.Advance(TimeSpan.FromSeconds(10));

        var warm = tracker.FilterWarm(["a", "b", "c"], 600);

        Assert.Equal(["b"], warm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void Tracker_FilterWarm_RuleNotApplicable_ReturnsLoadedUnchanged(double? idle)
    {
        var tracker = new ModelUsageTracker(new FakeTimeProvider());
        IReadOnlyCollection<string> loaded = ["a", "b"];

        Assert.Same(loaded, tracker.FilterWarm(loaded, idle));
        Assert.Null(tracker.FilterWarm(null, 600));
    }

    [Fact]
    public void Tracker_Clear_MakesEverythingFirstTime()
    {
        var tracker = new ModelUsageTracker(new FakeTimeProvider());
        tracker.MarkUsed(["a"]);
        Assert.False(tracker.IsCold("a", 600));

        tracker.Clear();

        Assert.Null(tracker.SinceLastUse("a"));
        Assert.True(tracker.IsCold("a", 600));
    }

    [Fact]
    public void Tracker_Describe_ListsStateWithoutUserContent()
    {
        var time = new FakeTimeProvider();
        var tracker = new ModelUsageTracker(time);
        tracker.MarkUsed(["a"]);
        time.Advance(TimeSpan.FromSeconds(612));

        Assert.Equal("a=冷(612s), b=冷(首次)", tracker.Describe(["a", "b", "a"], 600));
    }

    // ---- TimeoutRetry：超时重试一次且只一次 ----

    private static readonly TimeSpan First = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Cold = TimeSpan.FromMilliseconds(10000);

    private static EngineException TimeoutError(TimeSpan t) => new(EngineErrorKind.Timeout, "x") { Timeout = t };

    [Fact]
    public async Task Retry_FirstAttemptSucceeds_NoRetry()
    {
        var timeouts = new List<TimeSpan>();
        var retried = false;

        var result = await TimeoutRetry.ExecuteAsync(
            (t, _) =>
            {
                timeouts.Add(t);
                return Task.FromResult("ok");
            },
            First,
            Cold,
            (_, _) => retried = true,
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal([First], timeouts);
        Assert.False(retried);
    }

    [Fact]
    public async Task Retry_FirstTimesOut_RetriesOnceWithColdTimeout()
    {
        var timeouts = new List<TimeSpan>();
        (EngineException Error, TimeSpan Retry)? retried = null;

        var result = await TimeoutRetry.ExecuteAsync(
            (t, _) =>
            {
                timeouts.Add(t);
                return timeouts.Count == 1 ? Task.FromException<string>(TimeoutError(t)) : Task.FromResult("ok");
            },
            First,
            Cold,
            (ex, retry) => retried = (ex, retry),
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal([First, Cold], timeouts);
        Assert.NotNull(retried);
        Assert.Equal(First, retried.Value.Error.Timeout);
        Assert.Equal(Cold, retried.Value.Retry);
    }

    [Fact]
    public async Task Retry_TimesOutTwice_ThrowsSecondErrorAfterExactlyTwoAttempts()
    {
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<EngineException>(() => TimeoutRetry.ExecuteAsync<string>(
            (t, _) =>
            {
                attempts++;
                return Task.FromException<string>(TimeoutError(t));
            },
            First,
            Cold,
            null,
            CancellationToken.None));

        Assert.Equal(TimeoutRetry.MaxAttempts, attempts);
        Assert.Equal(EngineErrorKind.Timeout, ex.Kind);
        Assert.Equal(Cold, ex.Timeout);
    }

    [Theory]
    [InlineData(EngineErrorKind.Unavailable)]
    [InlineData(EngineErrorKind.UnsupportedPair)]
    [InlineData(EngineErrorKind.Internal)]
    [InlineData(EngineErrorKind.OcrUnavailable)]
    public async Task Retry_OtherErrors_NotRetried(EngineErrorKind kind)
    {
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<EngineException>(() => TimeoutRetry.ExecuteAsync<string>(
            (_, _) =>
            {
                attempts++;
                return Task.FromException<string>(new EngineException(kind, "x"));
            },
            First,
            Cold,
            null,
            CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task Retry_CallerCancels_NotRetried()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TimeoutRetry.ExecuteAsync<string>(
            (_, ct) =>
            {
                attempts++;
                cts.Cancel();
                return Task.FromCanceled<string>(ct);
            },
            First,
            Cold,
            (_, _) => Assert.Fail("取消时不应重试"),
            cts.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Retry_TimeoutRacingWithCancel_TreatedAsCancel()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TimeoutRetry.ExecuteAsync<string>(
            (t, _) =>
            {
                attempts++;
                cts.Cancel();
                return Task.FromException<string>(TimeoutError(t));
            },
            First,
            Cold,
            (_, _) => Assert.Fail("取消时不应重试"),
            cts.Token));

        Assert.Equal(1, attempts);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }
}
