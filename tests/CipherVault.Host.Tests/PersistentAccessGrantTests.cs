using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Grants;
using CipherVault.Core.Session;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class PersistentAccessGrantTests
{
    [Fact]
    public async Task IssuedGrantIsListedFromEncryptedVault()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-grant-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "vault");
        var vault = new VaultService(
            new AlwaysActiveActivityProvider());
        try
        {
            var initialized = await vault.InitializeAsync(
                vaultPath,
                "grant-store-password-2026");
            await vault.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            await vault.UnlockAsync(
                vaultPath,
                "grant-store-password-2026",
                VaultCredentialType.Password);

            var service = new AccessGrantService(store: vault);
            var now = DateTimeOffset.UtcNow;
            var issued = await service.RequestAsync(
                "mcp",
                Guid.NewGuid(),
                McpAccessMode.ReadOnly,
                AccessGrantScope.Read,
                new AccessGrantVaultGeneration(
                    (await vault.GetStatusAsync()).SessionId
                        ?? throw new InvalidOperationException(),
                    now.AddHours(1)),
                (_, _) => Task.FromResult(
                    new AccessGrantApprovalResult(
                        true,
                        AccessGrantScope.Read)),
                Guid.NewGuid());

            Assert.Equal(AccessGrantRequestStatus.Approved, issued.Status);
            var page = await service.ListStoredAsync(
                state: null,
                search: "mcp",
                cursor: null,
                limit: 50);

            var item = Assert.Single(page.Items);
            Assert.Equal(issued.IssuedGrant!.Record.GrantId, item.GrantId);
            Assert.Equal("active", item.State);
            Assert.Contains("read", item.Operations);
            Assert.Contains("read:*", item.ResourceScopes);
            Assert.Equal(1, page.Counts.Active);

            var detail = await service.GetStoredAsync(item.GrantId);
            Assert.Equal(item.GrantId, detail.GrantId);
            Assert.Equal("mcp", detail.ClientId);
            Assert.Equal("MCP", detail.ClientDisplayName);
            Assert.Contains("read", detail.Operations);
            Assert.Contains("read:*", detail.ResourceScopes);

            await vault.AppendDeniedAccessAsync(
                new AccessDeniedAudit(
                    "mcp",
                    issued.IssuedGrant!.Record.ClientInstanceId,
                    issued.IssuedGrant.Record.VerifiedProcessSessionId,
                    issued.IssuedGrant.Record.GrantId,
                    issued.IssuedGrant.Record.VaultSessionId,
                    "access_denied",
                    "read",
                    RequestId: null),
                DateTimeOffset.UtcNow);
            var deniedAudit = await vault.GetAuditPageAsync(
                new VaultAuditQuery(
                    Action: "access_denied",
                    Limit: 10));
            Assert.Contains(
                deniedAudit.Items,
                item => item.DetailsJson.Contains(
                    issued.IssuedGrant.Record.GrantId.ToString("D"),
                    StringComparison.Ordinal));

            await vault.UpdateLastUsedAsync(
                item.GrantId,
                now.AddMinutes(1),
                now.AddMinutes(30));
            var rollback = await Assert.ThrowsAsync<VaultException>(
                () => vault.GetAsync(
                    item.GrantId,
                    now.AddMinutes(-10)));
            Assert.Equal(VaultErrorCode.ClockRollback, rollback.Code);

            var record = issued.IssuedGrant.Record;
            var grantContext = new AccessGrantContext(
                record.GrantId,
                record.ClientId,
                record.ClientInstanceId,
                record.Scopes,
                record.VaultSessionId!.Value,
                record.VerifiedProcessSessionId,
                record.PathPolicy);
            Assert.Equal(
                AccessGrantValidationStatus.Valid,
                service.UseBridgeGrant(grantContext).Status);

            var activeB = await service.RequestAsync(
                "mcp",
                Guid.NewGuid(),
                McpAccessMode.ReadOnly,
                AccessGrantScope.Read,
                new AccessGrantVaultGeneration(
                    (await vault.GetStatusAsync()).SessionId
                        ?? throw new InvalidOperationException(),
                    DateTimeOffset.UtcNow.AddHours(1)),
                (_, _) => Task.FromResult(
                    new AccessGrantApprovalResult(
                        true,
                        AccessGrantScope.Read)),
                Guid.NewGuid());
            Assert.Equal(AccessGrantRequestStatus.Approved, activeB.Status);
            var contextB = new AccessGrantContext(
                activeB.IssuedGrant!.Record.GrantId,
                activeB.IssuedGrant.Record.ClientId,
                activeB.IssuedGrant.Record.ClientInstanceId,
                activeB.IssuedGrant.Record.Scopes,
                activeB.IssuedGrant.Record.VaultSessionId!.Value,
                activeB.IssuedGrant.Record.VerifiedProcessSessionId,
                activeB.IssuedGrant.Record.PathPolicy);

            var revoked = await service.RevokeStoredAsync(
                record.GrantId,
                new VaultAuditContext(VaultActorKind.UserUi, "CipherVault.Ui"));
            Assert.Equal("revoked", revoked.Outcome);
            Assert.NotNull(revoked.RevokedAt);
            Assert.Equal(
                AccessGrantValidationStatus.Revoked,
                service.UseBridgeGrant(grantContext).Status);
            Assert.Equal(
                AccessGrantValidationStatus.Valid,
                service.UseBridgeGrant(contextB).Status);

            var repeated = await service.RevokeStoredAsync(
                record.GrantId,
                new VaultAuditContext(VaultActorKind.UserUi, "CipherVault.Ui"));
            Assert.Equal("alreadyRevoked", repeated.Outcome);
            Assert.Equal(
                revoked.RevokedAt?.ToUnixTimeMilliseconds(),
                repeated.RevokedAt?.ToUnixTimeMilliseconds());

            var revokedPage = await service.ListStoredAsync(
                state: "revoked",
                search: "mcp",
                cursor: null,
                limit: 50);
            Assert.Equal(1, revokedPage.TotalCount);
            Assert.Equal("revoked", Assert.Single(revokedPage.Items).State);
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
    public async Task RevokeAllConsumesPreviewAndCancelsPendingGrant()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-grant-revoke-all-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "vault");
        var vault = new VaultService(new AlwaysActiveActivityProvider());
        try
        {
            var initialized = await vault.InitializeAsync(
                vaultPath,
                "grant-revoke-all-password-2026");
            await vault.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            var unlocked = await vault.UnlockAsync(
                vaultPath,
                "grant-revoke-all-password-2026",
                VaultCredentialType.Password);
            var vaultSessionId = Assert.IsType<Guid>(unlocked.SessionId);
            var now = DateTimeOffset.UtcNow;
            var service = new AccessGrantService(store: vault);
            var generation = new AccessGrantVaultGeneration(
                vaultSessionId,
                now.AddHours(1));

            var active = await service.RequestAsync(
                "mcp-a",
                Guid.NewGuid(),
                McpAccessMode.ReadOnly,
                AccessGrantScope.Read,
                generation,
                (_, _) => Task.FromResult(
                    new AccessGrantApprovalResult(
                        true,
                        AccessGrantScope.Read)),
                Guid.NewGuid());
            Assert.Equal(AccessGrantRequestStatus.Approved, active.Status);

            var approvalStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var approval = new TaskCompletionSource<AccessGrantApprovalResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingTask = service.RequestAsync(
                "mcp-b",
                Guid.NewGuid(),
                McpAccessMode.ReadOnly,
                AccessGrantScope.Read,
                generation,
                async (_, token) =>
                {
                    approvalStarted.TrySetResult();
                    return await approval.Task.WaitAsync(token);
                },
                Guid.NewGuid());
            await approvalStarted.Task;

            var adminSessionId = Guid.NewGuid();
            var preview = await service.PreviewRevokeAllStoredAsync(
                adminSessionId,
                vaultSessionId);
            Assert.Equal(1, preview.ActiveCount);
            Assert.Equal(1, preview.PendingCount);

            var result = await service.ExecuteRevokeAllStoredAsync(
                preview.PreviewToken,
                adminSessionId,
                vaultSessionId,
                new VaultAuditContext(VaultActorKind.UserUi, "CipherVault.Ui"));
            Assert.Equal(1, result.RevokedCount);
            Assert.Equal(1, result.CancelledPendingCount);

            var pendingOutcome = await pendingTask;
            Assert.Equal(AccessGrantRequestStatus.Denied, pendingOutcome.Status);
            Assert.Equal("cancelled", pendingOutcome.ReasonCode);
            approval.TrySetResult(
                new AccessGrantApprovalResult(
                    true,
                    AccessGrantScope.Read));

            var page = await service.ListStoredAsync(
                state: "revoked",
                search: null,
                cursor: null,
                limit: 50);
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(
                "revoked",
                Assert.Single(page.Items).State);

            var replay = await Assert.ThrowsAsync<AccessGrantPreviewException>(
                () => service.ExecuteRevokeAllStoredAsync(
                    preview.PreviewToken,
                    adminSessionId,
                    vaultSessionId,
                    new VaultAuditContext(VaultActorKind.UserUi, "CipherVault.Ui")));
            Assert.Equal("invalid_input", replay.Code);
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
    public async Task ExpirationSweepPersistsStateAndAudit()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-grant-expiry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "vault");
        var vault = new VaultService(new AlwaysActiveActivityProvider());
        try
        {
            var initialized = await vault.InitializeAsync(
                vaultPath,
                "grant-expiry-password-2026");
            await vault.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            var unlocked = await vault.UnlockAsync(
                vaultPath,
                "grant-expiry-password-2026",
                VaultCredentialType.Password);
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var service = new AccessGrantService(
                timeProvider: clock,
                idleLifetime: TimeSpan.FromMinutes(1),
                hardLifetime: TimeSpan.FromHours(1),
                store: vault);
            var issued = await service.RequestAsync(
                "mcp",
                Guid.NewGuid(),
                McpAccessMode.ReadOnly,
                AccessGrantScope.Read,
                new AccessGrantVaultGeneration(
                    Assert.IsType<Guid>(unlocked.SessionId),
                    clock.GetUtcNow().AddHours(1)),
                (_, _) => Task.FromResult(
                    new AccessGrantApprovalResult(
                        true,
                        AccessGrantScope.Read)),
                Guid.NewGuid());
            Assert.Equal(AccessGrantRequestStatus.Approved, issued.Status);

            clock.Advance(TimeSpan.FromMinutes(2));
            await service.SweepExpiredAsync();

            var page = await service.ListStoredAsync(
                state: "expired",
                search: "mcp",
                cursor: null,
                limit: 50);
            Assert.Equal(1, page.TotalCount);
            var audit = await vault.GetAuditPageAsync(
                new VaultAuditQuery(
                    Action: "grant_expired",
                    Limit: 10));
            Assert.Contains(audit.Items, item =>
                item.DetailsJson.Contains(
                    issued.IssuedGrant!.Record.GrantId.ToString("D"),
                    StringComparison.Ordinal));
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

        public void Advance(TimeSpan duration)
        {
            _now += duration;
        }
    }
}
