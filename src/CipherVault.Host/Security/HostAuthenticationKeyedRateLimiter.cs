namespace CipherVault.Host.Security;

public sealed class HostAuthenticationKeyedRateLimiter
{
    private const int FailureThreshold = 5;
    private const int MaximumTrackedKeys = 2_048;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(1);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, KeyState> _states =
        new(StringComparer.Ordinal);

    public HostAuthenticationKeyedRateLimiter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryGetBlockedUntil(string key, out DateTimeOffset blockedUntil)
    {
        lock (_sync)
        {
            blockedUntil = default;
            if (!_states.TryGetValue(key, out var state))
            {
                return false;
            }

            var now = _timeProvider.GetUtcNow();
            if (now < state.BlockedUntil)
            {
                blockedUntil = state.BlockedUntil;
                return true;
            }
            return false;
        }
    }

    public void RecordFailure(string key)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            PruneExpired(now);
            if (!_states.TryGetValue(key, out var state))
            {
                if (_states.Count >= MaximumTrackedKeys)
                {
                    RemoveOldest();
                }
                state = new KeyState();
                _states[key] = state;
            }

            state.Failures.RemoveAll(value => now - value > FailureWindow);
            state.Failures.Add(now);
            state.LastFailure = now;
            if (state.Failures.Count >= FailureThreshold)
            {
                state.BlockedUntil = now + BlockDuration;
            }
        }
    }

    public void RecordSuccess(string key)
    {
        lock (_sync)
        {
            _states.Remove(key);
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var (key, state) in _states)
        {
            if (state.BlockedUntil > now)
            {
                continue;
            }
            if (state.Failures.Count == 0 ||
                now - state.Failures[^1] > FailureWindow)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }
        foreach (var key in expired)
        {
            _states.Remove(key);
        }
    }

    private void RemoveOldest()
    {
        string? oldestKey = null;
        var oldest = DateTimeOffset.MaxValue;
        foreach (var (key, state) in _states)
        {
            if (state.LastFailure < oldest)
            {
                oldest = state.LastFailure;
                oldestKey = key;
            }
        }
        if (oldestKey is not null)
        {
            _states.Remove(oldestKey);
        }
    }

    private sealed class KeyState
    {
        public List<DateTimeOffset> Failures { get; } = [];
        public DateTimeOffset BlockedUntil { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastFailure { get; set; } = DateTimeOffset.MinValue;
    }
}
