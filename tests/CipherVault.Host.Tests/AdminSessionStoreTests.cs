using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class AdminSessionStoreTests
{
    [Fact]
    public void SessionExpiresAtIdleLifetime()
    {
        var now = DateTimeOffset.Parse("2026-10-22T10:00:00Z");
        var time = new MutableTimeProvider(now);
        var store = new AdminSessionStore(
            time,
            idleLifetime: TimeSpan.FromSeconds(10),
            hardLifetime: TimeSpan.FromSeconds(30));
        var session = store.Create(
            AdminIdentity(VerifiedProcessKind.AdminUi),
            "S-1-5-21-owner",
            "S-1-5-80-service");

        Assert.True(store.TryGetActive(session.SessionId, out _));
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.False(store.TryGetActive(session.SessionId, out var expired));
        Assert.Equal(AdminSessionState.Expired, expired.State);
    }

    [Fact]
    public void RevokeAllOnlyRevokesActiveSessions()
    {
        var store = new AdminSessionStore();
        var first = store.Create(
            AdminIdentity(VerifiedProcessKind.AdminUi),
            "S-1-5-21-owner",
            "S-1-5-80-service");
        var second = store.Create(
            AdminIdentity(VerifiedProcessKind.AdminCli),
            "S-1-5-21-owner",
            "S-1-5-80-service");

        var revoked = store.RevokeAll("vault-locked");

        Assert.Equal(2, revoked);
        Assert.All(
            store.List(),
            session => Assert.Equal(AdminSessionState.Revoked, session.State));
        Assert.False(store.TryGetActive(first.SessionId, out _));
        Assert.False(store.TryGetActive(second.SessionId, out _));
    }

    [Fact]
    public void VaultSessionCanBeBoundAfterUnlock()
    {
        var store = new AdminSessionStore();
        var session = store.Create(
            AdminIdentity(VerifiedProcessKind.AdminUi),
            "S-1-5-21-owner",
            "S-1-5-80-service");
        var vaultSessionId = Guid.NewGuid();

        Assert.True(store.SetVaultSession(session.SessionId, vaultSessionId));
        Assert.True(store.TryGetActive(session.SessionId, out var active));
        Assert.Equal(vaultSessionId, active.VaultSessionId);
    }

    private static VerifiedProcessIdentity AdminIdentity(
        VerifiedProcessKind kind)
    {
        return new VerifiedProcessIdentity(
            kind,
            10,
            DateTimeOffset.Parse("2026-10-22T09:00:00Z"),
            "S-1-5-21-owner",
            "S-1-5-21-logon",
            1,
            @"C:\Program Files\CipherVault\client.exe",
            new string('a', 64),
            new string('b', 64),
            "CN=Test",
            IntegrityLevel: 8192);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan duration)
        {
            _now += duration;
        }
    }
}
