using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Grants;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
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
            var pendingGrantId = service.List()
                .Single(item => item.State == AccessGrantState.Pending)
                .GrantId;
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
            var cancellationAudit = await vault.GetAuditPageAsync(
                new VaultAuditQuery(Action: "grant_cancelled", Limit: 10));
            Assert.Contains(
                cancellationAudit.Items,
                item => item.DetailsJson.Contains(
                    pendingGrantId.ToString("D"),
                    StringComparison.Ordinal));
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

    [Fact]
    public async Task PendingCancellationWritesAuditAndDeniesApproval()
    {
        await using var environment = await GrantTestVault.CreateAsync();
        using var service = new AccessGrantService(store: environment.Vault);
        var active = await RequestReadGrantAsync(
            environment,
            service,
            clientId: "mcp-active");
        var approvalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<AccessGrantApprovalResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processSessionId = Guid.NewGuid();
        var pendingTask = service.RequestAsync(
            "mcp",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            new AccessGrantVaultGeneration(
                environment.VaultSessionId,
                DateTimeOffset.UtcNow.AddHours(1)),
            async (_, token) =>
            {
                approvalStarted.TrySetResult();
                return await approval.Task.WaitAsync(token);
            },
            processSessionId);
        await approvalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pendingId = service.List()
            .Single(item => item.State == AccessGrantState.Pending)
            .GrantId;

        var cancelled = await service.RevokeStoredAsync(
            pendingId,
            new VaultAuditContext(VaultActorKind.UserUi, "CipherVault.Ui"));

        Assert.Equal("cancelled", cancelled.Outcome);
        Assert.Contains(
            service.List(),
            item => item.GrantId == active.GrantId &&
                item.State == AccessGrantState.Active);
        var outcome = await pendingTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AccessGrantRequestStatus.Denied, outcome.Status);
        Assert.Equal("cancelled", outcome.ReasonCode);
        var audit = await environment.Vault.GetAuditPageAsync(
            new VaultAuditQuery(Action: "grant_cancelled", Limit: 10));
        var item = Assert.Single(audit.Items);
        Assert.Contains(
            pendingId.ToString("D"),
            item.DetailsJson,
            StringComparison.Ordinal);
        Assert.Contains(
            processSessionId.ToString("D"),
            item.DetailsJson,
            StringComparison.Ordinal);
        approval.TrySetResult(new AccessGrantApprovalResult(
            true,
            AccessGrantScope.Read));
    }

    [Fact]
    public async Task ConcurrentRevokeTransitionsGrantExactlyOnce()
    {
        await using var environment = await GrantTestVault.CreateAsync();
        using var service = new AccessGrantService(store: environment.Vault);
        var issued = await RequestReadGrantAsync(environment, service);
        var auditContext = new VaultAuditContext(
            VaultActorKind.UserUi,
            "CipherVault.Ui");

        var results = await Task.WhenAll(
            service.RevokeStoredAsync(issued.GrantId, auditContext),
            service.RevokeStoredAsync(issued.GrantId, auditContext));

        Assert.Contains(results, item => item.Outcome == "revoked");
        Assert.Contains(results, item => item.Outcome == "alreadyRevoked");
        var audit = await environment.Vault.GetAuditPageAsync(
            new VaultAuditQuery(Action: "grant_revoked", Limit: 10));
        var revoked = Assert.Single(audit.Items);
        Assert.Contains(
            issued.GrantId.ToString("D"),
            revoked.DetailsJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokeAllStorageFailureDoesNotCancelPendingGrant()
    {
        await using var environment = await GrantTestVault.CreateAsync();
        var store = new RevokeFailingStore(environment.Vault);
        var securityLog = new RecordingSecurityLog();
        using var service = new AccessGrantService(
            securityLog: securityLog,
            store: store);
        _ = await RequestReadGrantAsync(
            environment,
            service,
            clientId: "mcp-active");
        var approvalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<AccessGrantApprovalResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingTask = service.RequestAsync(
            "mcp",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            new AccessGrantVaultGeneration(
                environment.VaultSessionId,
                DateTimeOffset.UtcNow.AddHours(1)),
            async (_, token) =>
            {
                approvalStarted.TrySetResult();
                return await approval.Task.WaitAsync(token);
            },
            Guid.NewGuid());
        await approvalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var adminSessionId = Guid.NewGuid();
        var preview = await service.PreviewRevokeAllStoredAsync(
            adminSessionId,
            environment.VaultSessionId);
        Assert.Equal(1, preview.ActiveCount);
        Assert.Equal(1, preview.PendingCount);
        store.FailRevokeAll = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteRevokeAllStoredAsync(
                preview.PreviewToken,
                adminSessionId,
                environment.VaultSessionId,
                new VaultAuditContext(VaultActorKind.UserUi, "CipherVault.Ui")));

        Assert.Equal(2, service.List().Count);
        Assert.Contains(
            service.List(),
            item => item.ClientId == "mcp-active" &&
                item.State == AccessGrantState.Active);
        Assert.Contains(
            service.List(),
            item => item.State == AccessGrantState.Pending);
        Assert.Contains(
            securityLog.Events,
            item => item.EventType == "grant_revoke_all_failed" &&
                item.Result == "failed" &&
                item.ReasonCode == "storage_error");
        approval.SetResult(new AccessGrantApprovalResult(
            true,
            AccessGrantScope.Read));
        var approved = await pendingTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AccessGrantRequestStatus.Approved, approved.Status);
        store.FailRevokeAll = false;
        await service.RevokeAllAsync("test-cleanup");
    }

    [Fact]
    public async Task RevokeAllPreviewRejectsExpiryAndSessionChange()
    {
        await using var environment = await GrantTestVault.CreateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var service = new AccessGrantService(
            timeProvider: clock,
            store: environment.Vault);
        _ = await RequestReadGrantAsync(environment, service);
        var adminSessionId = Guid.NewGuid();
        var auditContext = new VaultAuditContext(
            VaultActorKind.UserUi,
            "CipherVault.Ui");

        var expiredPreview = await service.PreviewRevokeAllStoredAsync(
            adminSessionId,
            environment.VaultSessionId);
        clock.Advance(TimeSpan.FromMinutes(3));
        var expired = await Assert.ThrowsAsync<AccessGrantPreviewException>(() =>
            service.ExecuteRevokeAllStoredAsync(
                expiredPreview.PreviewToken,
                adminSessionId,
                environment.VaultSessionId,
                auditContext));
        Assert.Equal("invalid_input", expired.Code);

        var wrongAdminPreview = await service.PreviewRevokeAllStoredAsync(
            adminSessionId,
            environment.VaultSessionId);
        var wrongAdmin = await Assert.ThrowsAsync<AccessGrantPreviewException>(() =>
            service.ExecuteRevokeAllStoredAsync(
                wrongAdminPreview.PreviewToken,
                Guid.NewGuid(),
                environment.VaultSessionId,
                auditContext));
        Assert.Equal("invalid_input", wrongAdmin.Code);

        var wrongVaultPreview = await service.PreviewRevokeAllStoredAsync(
            adminSessionId,
            environment.VaultSessionId);
        var wrongVault = await Assert.ThrowsAsync<AccessGrantPreviewException>(() =>
            service.ExecuteRevokeAllStoredAsync(
                wrongVaultPreview.PreviewToken,
                adminSessionId,
                Guid.NewGuid(),
                auditContext));
        Assert.Equal("invalid_input", wrongVault.Code);

        var oversized = await Assert.ThrowsAsync<AccessGrantPreviewException>(() =>
            service.ExecuteRevokeAllStoredAsync(
                new string('x', 513),
                adminSessionId,
                environment.VaultSessionId,
                auditContext));
        Assert.Equal("invalid_input", oversized.Code);
    }

    [Fact]
    public async Task RevokeAllRejectsNewGrantAfterPreview()
    {
        await using var environment = await GrantTestVault.CreateAsync();
        using var service = new AccessGrantService(store: environment.Vault);
        _ = await RequestReadGrantAsync(environment, service);
        var adminSessionId = Guid.NewGuid();
        var preview = await service.PreviewRevokeAllStoredAsync(
            adminSessionId,
            environment.VaultSessionId);
        _ = await RequestReadGrantAsync(
            environment,
            service,
            clientId: "mcp-new");

        var changed = await Assert.ThrowsAsync<AccessGrantPreviewException>(() =>
            service.ExecuteRevokeAllStoredAsync(
                preview.PreviewToken,
                adminSessionId,
                environment.VaultSessionId,
                new VaultAuditContext(VaultActorKind.UserUi, "CipherVault.Ui")));

        Assert.Equal("grants_changed", changed.Code);
        Assert.Equal(2, service.List().Count);
    }

    [Fact]
    public async Task RevokeAllReportsGrantsRevokedAfterPreview()
    {
        await using var environment = await GrantTestVault.CreateAsync();
        using var service = new AccessGrantService(store: environment.Vault);
        var first = await RequestReadGrantAsync(
            environment,
            service,
            clientId: "mcp-a");
        _ = await RequestReadGrantAsync(
            environment,
            service,
            clientId: "mcp-b");
        var adminSessionId = Guid.NewGuid();
        var auditContext = new VaultAuditContext(
            VaultActorKind.UserUi,
            "CipherVault.Ui");
        var preview = await service.PreviewRevokeAllStoredAsync(
            adminSessionId,
            environment.VaultSessionId);
        Assert.Equal(2, preview.ActiveCount);

        _ = await service.RevokeStoredAsync(first.GrantId, auditContext);
        var result = await service.ExecuteRevokeAllStoredAsync(
            preview.PreviewToken,
            adminSessionId,
            environment.VaultSessionId,
            auditContext);

        Assert.Equal(1, result.RevokedCount);
        Assert.Equal(1, result.AlreadyRevokedCount);
        Assert.Equal(0, result.AlreadyExpiredCount);
    }

    [Fact]
    public async Task HostSessionInitializationRevokesStaleGrantAfterClockRollback()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        await using var environment = await GrantTestVault.CreateAsync(clock);
        using var issuingService = new AccessGrantService(
            timeProvider: clock,
            store: environment.Vault);
        var issued = await RequestReadGrantAsync(environment, issuingService);
        clock.Advance(TimeSpan.FromMinutes(10));
        await environment.Vault.UpdateLastUsedAsync(
            issued.GrantId,
            clock.GetUtcNow(),
            clock.GetUtcNow().AddMinutes(30));
        clock.Set(now.AddMinutes(4));
        using var restartedService = new AccessGrantService(
            timeProvider: clock,
            store: environment.Vault);

        await restartedService.InitializeStoredSessionAsync();

        clock.Set(now.AddMinutes(11));
        var detail = await environment.Vault.GetAsync(
            issued.GrantId,
            clock.GetUtcNow());
        Assert.NotNull(detail);
        Assert.Equal(StoredAccessGrantState.Revoked, detail.State);
        Assert.Equal("host-restarted", detail.RevocationReason);
    }

    private static async Task<StoredAccessGrant> RequestReadGrantAsync(
        GrantTestVault environment,
        AccessGrantService service,
        string clientId = "mcp")
    {
        var outcome = await service.RequestAsync(
            clientId,
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            new AccessGrantVaultGeneration(
                environment.VaultSessionId,
                DateTimeOffset.UtcNow.AddHours(1)),
            (_, _) => Task.FromResult(new AccessGrantApprovalResult(
                true,
                AccessGrantScope.Read)),
            Guid.NewGuid());
        Assert.Equal(AccessGrantRequestStatus.Approved, outcome.Status);
        var stored = await environment.Vault.GetAsync(
            outcome.IssuedGrant!.Record.GrantId,
            DateTimeOffset.UtcNow);
        return stored
            ?? throw new InvalidOperationException(
                "Approved Access Grant was not persisted.");
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

        public void Set(DateTimeOffset now)
        {
            _now = now;
        }
    }

    private sealed class GrantTestVault : IAsyncDisposable
    {
        private const string Password = "persistent-grant-password-2026";

        private GrantTestVault(
            string root,
            VaultService vault,
            Guid vaultSessionId)
        {
            Root = root;
            Vault = vault;
            VaultSessionId = vaultSessionId;
        }

        public string Root { get; }
        public VaultService Vault { get; }
        public Guid VaultSessionId { get; }

        public static async Task<GrantTestVault> CreateAsync(
            TimeProvider? timeProvider = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-grant-state-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultPath = Path.Combine(root, "vault");
            var vault = new VaultService(
                new AlwaysActiveActivityProvider(),
                timeProvider);
            try
            {
                var initialized = await vault.InitializeAsync(
                    vaultPath,
                    Password);
                await vault.ConfirmRecoveryKeyAsync(
                    vaultPath,
                    initialized.RecoveryKey,
                    VaultActorKind.UserUi);
                var unlocked = await vault.UnlockAsync(
                    vaultPath,
                    Password,
                    VaultCredentialType.Password);
                return new GrantTestVault(
                    root,
                    vault,
                    Assert.IsType<Guid>(unlocked.SessionId));
            }
            catch
            {
                await vault.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Vault.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RevokeFailingStore(IAccessGrantStore inner)
        : IAccessGrantStore
    {
        public bool FailRevokeAll { get; set; }

        public Task CreateAsync(
            StoredAccessGrant grant,
            VaultAuditContext auditContext,
            CancellationToken cancellationToken = default)
        {
            return inner.CreateAsync(grant, auditContext, cancellationToken);
        }

        public Task<StoredAccessGrantPage> ListAsync(
            StoredAccessGrantQuery query,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            return inner.ListAsync(query, now, cancellationToken);
        }

        public Task<StoredAccessGrant?> GetAsync(
            Guid grantId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            return inner.GetAsync(grantId, now, cancellationToken);
        }

        public Task<IReadOnlyList<StoredAccessGrant>> GetActiveGrantsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            return inner.GetActiveGrantsAsync(now, cancellationToken);
        }

        public Task<StoredAccessGrantRevokeOutcome> RevokeAsync(
            Guid grantId,
            string reason,
            DateTimeOffset now,
            VaultAuditContext auditContext,
            CancellationToken cancellationToken = default)
        {
            return inner.RevokeAsync(
                grantId,
                reason,
                now,
                auditContext,
                cancellationToken);
        }

        public Task<StoredAccessGrantRevokeAllOutcome> RevokeAllAsync(
            IReadOnlyCollection<Guid> activeGrantIds,
            IReadOnlyCollection<StoredAccessGrantCancellation> pendingCancellations,
            string reason,
            DateTimeOffset now,
            VaultAuditContext auditContext,
            CancellationToken cancellationToken = default)
        {
            if (FailRevokeAll)
            {
                throw new InvalidOperationException(
                    "Simulated Access Grant storage failure.");
            }
            return inner.RevokeAllAsync(
                activeGrantIds,
                pendingCancellations,
                reason,
                now,
                auditContext,
                cancellationToken);
        }

        public Task AppendPendingCancellationAsync(
            StoredAccessGrantCancellation cancellation,
            VaultAuditContext auditContext,
            CancellationToken cancellationToken = default)
        {
            if (FailRevokeAll)
            {
                throw new InvalidOperationException(
                    "Simulated Access Grant storage failure.");
            }
            return inner.AppendPendingCancellationAsync(
                cancellation,
                auditContext,
                cancellationToken);
        }

        public Task<IReadOnlyList<Guid>> ExpireAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            return inner.ExpireAsync(now, cancellationToken);
        }

        public Task<IReadOnlyList<Guid>> RevokeStaleActiveAsync(
            string reason,
            DateTimeOffset now,
            VaultAuditContext auditContext,
            CancellationToken cancellationToken = default)
        {
            return inner.RevokeStaleActiveAsync(
                reason,
                now,
                auditContext,
                cancellationToken);
        }

        public Task<bool> UpdateLastUsedAsync(
            Guid grantId,
            DateTimeOffset usedAt,
            DateTimeOffset idleExpiresAt,
            CancellationToken cancellationToken = default)
        {
            return inner.UpdateLastUsedAsync(
                grantId,
                usedAt,
                idleExpiresAt,
                cancellationToken);
        }

        public Task EnsureClockAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            return inner.EnsureClockAsync(now, cancellationToken);
        }

        public Task AppendDeniedAccessAsync(
            AccessDeniedAudit audit,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            return inner.AppendDeniedAccessAsync(audit, now, cancellationToken);
        }
    }

    private sealed class RecordingSecurityLog : ISecurityEventLog
    {
        public List<HostSecurityLogEvent> Events { get; } = [];

        public Task WriteAsync(
            HostSecurityLogEvent securityEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(securityEvent);
            return Task.CompletedTask;
        }
    }
}
