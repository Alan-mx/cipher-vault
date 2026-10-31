namespace CipherVault.Server.Security;

public sealed record UnlockAttemptStatus(bool CanAttempt, int RetryAfterSeconds);

public sealed class UnlockAttemptLimiter
{
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    private readonly object sync = new();
    private AttemptState? state;

    public UnlockAttemptStatus GetStatus(DateTimeOffset now)
    {
        lock (sync)
        {
            ResetExpired(now);
            if (state?.CooldownUntil is { } cooldownUntil
                && now < cooldownUntil)
            {
                return new UnlockAttemptStatus(
                    CanAttempt: false,
                    RetryAfterSeconds: Math.Max(
                        1,
                        (int)Math.Ceiling((cooldownUntil - now).TotalSeconds)));
            }

            return new UnlockAttemptStatus(
                CanAttempt: true,
                RetryAfterSeconds: 0);
        }
    }

    public void RecordFailure(DateTimeOffset now)
    {
        lock (sync)
        {
            ResetExpired(now);
            var current = state ?? new AttemptState(now, 0, null);
            var failures = current.FailedAttempts + 1;
            state = failures >= 5
                ? current with
                {
                    FailedAttempts = failures,
                    CooldownUntil = now + Cooldown
                }
                : current with { FailedAttempts = failures };
        }
    }

    public void RecordSuccess()
    {
        lock (sync)
        {
            state = null;
        }
    }

    private void ResetExpired(DateTimeOffset now)
    {
        if (state is null)
        {
            return;
        }

        if (state.CooldownUntil is { } cooldownUntil && now >= cooldownUntil)
        {
            state = null;
            return;
        }

        if (now - state.WindowStartedAt >= FailureWindow)
        {
            state = null;
        }
    }

    private sealed record AttemptState(
        DateTimeOffset WindowStartedAt,
        int FailedAttempts,
        DateTimeOffset? CooldownUntil);
}
