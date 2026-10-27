using System.Security.Cryptography;
using CipherVault.Core.Documents;
using CipherVault.Core.Grants;
using CipherVault.Core.Session;

namespace CipherVault.Core.Tests;

public sealed class AccessGrantStoreTests
{
    [Fact]
    public async Task ClockRollbackDoesNotPreventTerminalRevocation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-grant-clock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "vault");
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        await using var vault = new VaultService(
            new AlwaysActiveActivityProvider(),
            clock);
        try
        {
            var initialized = await vault.InitializeAsync(
                vaultPath,
                "grant-clock-password-2026");
            await vault.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            var unlocked = await vault.UnlockAsync(
                vaultPath,
                "grant-clock-password-2026",
                VaultCredentialType.Password);
            var vaultSessionId = Assert.IsType<Guid>(unlocked.SessionId);
            var first = CreateGrant(vaultSessionId, now, "mcp-a");
            var second = CreateGrant(vaultSessionId, now, "mcp-b");
            await vault.CreateAsync(
                first,
                new VaultAuditContext(VaultActorKind.Mcp, "mcp-a"));
            await vault.CreateAsync(
                second,
                new VaultAuditContext(VaultActorKind.Mcp, "mcp-b"));

            await vault.UpdateLastUsedAsync(
                first.GrantId,
                now.AddMinutes(10),
                now.AddMinutes(40));
            clock.Set(now.AddMinutes(4));

            var revoked = await vault.RevokeAsync(
                first.GrantId,
                "admin-requested",
                clock.GetUtcNow(),
                new VaultAuditContext(VaultActorKind.UserUi, "CipherVault.Ui"));
            Assert.Equal(StoredAccessGrantRevokeStatus.Revoked, revoked.Status);

            var stale = await vault.RevokeStaleActiveAsync(
                "host-restarted",
                clock.GetUtcNow(),
                new VaultAuditContext(VaultActorKind.System, "CipherVault.Host"));
            Assert.Equal(second.GrantId, Assert.Single(stale));

            clock.Set(now.AddMinutes(11));
            var firstDetail = await vault.GetAsync(
                first.GrantId,
                clock.GetUtcNow());
            var secondDetail = await vault.GetAsync(
                second.GrantId,
                clock.GetUtcNow());
            Assert.Equal(StoredAccessGrantState.Revoked, firstDetail?.State);
            Assert.Equal(StoredAccessGrantState.Revoked, secondDetail?.State);
        }
        finally
        {
            await vault.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EncryptedBackupPreservesGrantHistoryAndStartupRevocation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-grant-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "vault");
        var backupPath = Path.Combine(root, "vault.cvbackup");
        var restoredPath = Path.Combine(root, "restored");
        var vault = new VaultService(new AlwaysActiveActivityProvider());
        try
        {
            var initialized = await vault.InitializeAsync(
                vaultPath,
                "grant-backup-password-2026");
            await vault.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            var unlocked = await vault.UnlockAsync(
                vaultPath,
                "grant-backup-password-2026",
                VaultCredentialType.Password);
            var now = DateTimeOffset.UtcNow;
            var grant = new StoredAccessGrant(
                Guid.NewGuid(),
                "mcp",
                "MCP",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.Empty,
                Assert.IsType<Guid>(unlocked.SessionId),
                StoredAccessGrantState.Active,
                Scopes: 1,
                RequestedScopes: 1,
                PathPolicyJson: """{"version":1}""",
                now,
                now,
                LastUsedAt: null,
                now.AddMinutes(30),
                now.AddHours(4),
                RevokedAt: null,
                RevocationReason: null,
                new byte[32]);
            await vault.CreateAsync(
                grant,
                new VaultAuditContext(
                    VaultActorKind.Mcp,
                    "mcp",
                    GrantId: grant.GrantId,
                    GrantScopes: "read"));
            Assert.True(File.Exists(
                (await vault.CreateBackupAsync(
                    backupPath,
                    VaultActorKind.UserUi)).BackupPath));
            var restored = await vault.RestoreBackupAsync(
                backupPath,
                restoredPath,
                "grant-backup-password-2026",
                VaultCredentialType.Password,
                VaultActorKind.UserUi);

            await using var restoredVault = new VaultService(
                new AlwaysActiveActivityProvider());
            var restoredStatus = await restoredVault.UnlockAsync(
                restored.VaultPath,
                "grant-backup-password-2026",
                VaultCredentialType.Password);
            var page = await restoredVault.ListAsync(
                new StoredAccessGrantQuery(
                    State: "active",
                    Search: "mcp",
                    Cursor: null,
                    Limit: 50),
                now);
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(grant.GrantId, Assert.Single(page.Items).GrantId);

            await restoredVault.RevokeStaleActiveAsync(
                "host-restarted",
                now.AddMinutes(1),
                new VaultAuditContext(
                    VaultActorKind.System,
                    "CipherVault.Host"));
            var revoked = await restoredVault.GetAsync(
                grant.GrantId,
                now.AddMinutes(2));
            Assert.NotNull(revoked);
            Assert.Equal(StoredAccessGrantState.Revoked, revoked.State);
            Assert.Equal("host-restarted", revoked.RevocationReason);
            Assert.Equal(
                VaultSessionState.Unlocked,
                restoredStatus.State);
        }
        finally
        {
            await vault.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Set(DateTimeOffset now)
        {
            _now = now;
        }
    }

    private static StoredAccessGrant CreateGrant(
        Guid vaultSessionId,
        DateTimeOffset now,
        string clientId)
    {
        return new StoredAccessGrant(
            Guid.NewGuid(),
            clientId,
            "MCP",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.Empty,
            vaultSessionId,
            StoredAccessGrantState.Active,
            Scopes: 1,
            RequestedScopes: 1,
            PathPolicyJson: """{"version":1}""",
            now,
            now,
            LastUsedAt: null,
            now.AddMinutes(30),
            now.AddHours(4),
            RevokedAt: null,
            RevocationReason: null,
            RandomNumberGenerator.GetBytes(32));
    }
}
