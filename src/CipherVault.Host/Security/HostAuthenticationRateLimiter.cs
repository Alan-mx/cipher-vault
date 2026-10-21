namespace CipherVault.Host.Security;

public sealed class HostAuthenticationRateLimiter
{
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(1);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _failureThreshold;
    private readonly List<DateTimeOffset> _failures = [];
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public HostAuthenticationRateLimiter(
        TimeProvider? timeProvider = null,
        int failureThreshold = 5)
    {
        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        }
        _timeProvider = timeProvider ?? TimeProvider.System;
        _failureThreshold = failureThreshold;
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
            if (_failures.Count >= _failureThreshold)
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
