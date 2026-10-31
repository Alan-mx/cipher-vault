namespace CipherVault.Server.Security;

public sealed record ActivityTouchStatus(
    bool Allowed,
    int RetryAfterSeconds);

public sealed class ActivityTouchLimiter
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly object sync = new();
    private readonly TimeProvider timeProvider;
    private Guid? sessionId;
    private long? lastAcceptedTimestamp;

    public ActivityTouchLimiter(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    public ActivityTouchStatus TryAcquire(Guid currentSessionId)
    {
        lock (sync)
        {
            var timestamp = timeProvider.GetTimestamp();
            if (sessionId != currentSessionId
                || lastAcceptedTimestamp is null)
            {
                sessionId = currentSessionId;
                lastAcceptedTimestamp = timestamp;
                return new ActivityTouchStatus(
                    Allowed: true,
                    RetryAfterSeconds: 0);
            }

            var elapsed = timeProvider.GetElapsedTime(
                lastAcceptedTimestamp.Value,
                timestamp);
            if (elapsed >= Interval)
            {
                lastAcceptedTimestamp = timestamp;
                return new ActivityTouchStatus(
                    Allowed: true,
                    RetryAfterSeconds: 0);
            }

            return new ActivityTouchStatus(
                Allowed: false,
                RetryAfterSeconds: Math.Max(
                    1,
                    (int)Math.Ceiling(
                        (Interval - elapsed).TotalSeconds)));
        }
    }
}
