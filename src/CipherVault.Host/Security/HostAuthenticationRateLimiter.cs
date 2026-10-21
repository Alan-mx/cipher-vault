namespace CipherVault.Host.Security;

public sealed class HostAuthenticationRateLimiter
{
    private const int FailureThreshold = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(1);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly List<DateTimeOffset> _failures = [];
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public HostAuthenticationRateLimiter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryGetBlockedUntil(out DateTimeOffset blockedUntil)
    {
        lock (_sync)
        {
            blockedUntil = _blockedUntil;
            return _timeProvider.GetUtcNow() < blockedUntil;
        }
    }

    public void RecordFailure()
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            _failures.RemoveAll(value => now - value > FailureWindow);
            _failures.Add(now);
            if (_failures.Count >= FailureThreshold)
            {
                _blockedUntil = now + BlockDuration;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_sync)
        {
            _failures.Clear();
            _blockedUntil = DateTimeOffset.MinValue;
        }
    }
}
