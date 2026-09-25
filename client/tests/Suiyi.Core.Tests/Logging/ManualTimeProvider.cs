namespace Suiyi.Core.Tests.Logging;

/// <summary>手动推进的时钟，本地时区固定为 UTC，避免依赖运行机器的时区。</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
