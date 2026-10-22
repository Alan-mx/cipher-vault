using CipherVault.Host.Contracts;

namespace CipherVault.Host.Security;

public enum AdminSessionState
{
    Active,
    Revoked,
    Expired
}

public sealed record AdminSession(
    Guid SessionId,
    Guid ProcessSessionId,
    VerifiedProcessIdentity Client,
    string OwnerSid,
    string ServiceSid,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset HardExpiresAt,
    Guid? VaultSessionId,
    AdminSessionState State,
    string? RevocationReason);

public sealed class AdminSessionStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, AdminSession> _sessions = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleLifetime;
    private readonly TimeSpan _hardLifetime;

    public AdminSessionStore(
        TimeProvider? timeProvider = null,
        TimeSpan? idleLifetime = null,
        TimeSpan? hardLifetime = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleLifetime = idleLifetime ?? TimeSpan.FromMinutes(15);
        _hardLifetime = hardLifetime ?? TimeSpan.FromHours(4);
        if (_idleLifetime <= TimeSpan.Zero ||
            _hardLifetime < _idleLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleLifetime),
                "Admin session lifetimes are invalid.");
        }
    }

    public AdminSession Create(
        VerifiedProcessIdentity client,
        string ownerSid,
        string serviceSid,
        Guid? vaultSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceSid);
        if (client.Kind is not (
                VerifiedProcessKind.AdminUi or
                VerifiedProcessKind.AdminCli))
        {
            throw new ArgumentException(
                "Admin session requires a UI or CLI process.",
                nameof(client));
        }

        var now = _timeProvider.GetUtcNow();
        var hardExpiresAt = now + _hardLifetime;
        var idleExpiresAt = Min(now + _idleLifetime, hardExpiresAt);
        var session = new AdminSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            client,
            ownerSid,
            serviceSid,
            now,
            now,
            idleExpiresAt,
            hardExpiresAt,
            vaultSessionId,
            AdminSessionState.Active,
            RevocationReason: null);
        lock (_sync)
        {
            _sessions[session.SessionId] = session;
        }
        return session;
    }

    public bool TryGetActive(Guid sessionId, out AdminSession session)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out session!))
            {
                return false;
            }
            var now = _timeProvider.GetUtcNow();
            if (session.State != AdminSessionState.Active ||
                now >= session.IdleExpiresAt ||
                now >= session.HardExpiresAt)
            {
                session = session with
                {
                    State = AdminSessionState.Expired,
                    RevocationReason = "admin-session-expired"
                };
                _sessions[sessionId] = session;
                return false;
            }

            var idleExpiresAt = Min(now + _idleLifetime, session.HardExpiresAt);
            session = session with
            {
                LastUsedAt = now,
                IdleExpiresAt = idleExpiresAt
            };
            _sessions[sessionId] = session;
            return true;
        }
    }

    public bool SetVaultSession(Guid sessionId, Guid? vaultSessionId)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) ||
                session.State != AdminSessionState.Active)
            {
                return false;
            }
            _sessions[sessionId] = session with
            {
                VaultSessionId = vaultSessionId
            };
            return true;
        }
    }

    public bool Revoke(Guid sessionId, string reason)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return false;
            }
            _sessions[sessionId] = session with
            {
                State = AdminSessionState.Revoked,
                RevocationReason = reason
            };
            return true;
        }
    }

    public int RevokeAll(string reason)
    {
        lock (_sync)
        {
            var revoked = 0;
            foreach (var pair in _sessions.ToArray())
            {
                if (pair.Value.State == AdminSessionState.Active)
                {
                    _sessions[pair.Key] = pair.Value with
                    {
                        State = AdminSessionState.Revoked,
                        RevocationReason = reason
                    };
                    revoked++;
                }
            }
            return revoked;
        }
    }

    public IReadOnlyList<AdminSession> List()
    {
        lock (_sync)
        {
            return _sessions.Values.ToArray();
        }
    }

    private static DateTimeOffset Min(
        DateTimeOffset first,
        DateTimeOffset second)
    {
        return first <= second ? first : second;
    }
}
