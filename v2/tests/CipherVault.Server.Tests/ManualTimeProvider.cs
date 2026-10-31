namespace CipherVault.Server.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private long timestamp;
    private DateTimeOffset utcNow;

    internal ManualTimeProvider(DateTimeOffset? startUtc = null)
    {
        utcNow = startUtc ?? new DateTimeOffset(
            2026,
            10,
            31,
            0,
            0,
            0,
            TimeSpan.Zero);
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => timestamp;

    public override DateTimeOffset GetUtcNow() => utcNow;

    internal void Advance(TimeSpan duration)
    {
        timestamp += duration.Ticks;
        utcNow += duration;
    }

    internal void MoveWallClock(TimeSpan offset) => utcNow += offset;
}
