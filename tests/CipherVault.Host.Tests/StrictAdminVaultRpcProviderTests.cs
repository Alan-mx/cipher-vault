using System.IO;
using System.Security.Principal;
using System.Text.Json;
using CipherVault.Core.Backup;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Service;
using CipherVault.Host.Session;
using Microsoft.Extensions.DependencyInjection;

namespace CipherVault.Host.Tests;

public sealed class StrictAdminVaultRpcProviderTests
{
    [Fact]
    public async Task CreateNoteExecutesThroughVaultGateway()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var sessions = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        await sessions.UnlockAsync(HostCredentialPreference.Password);
        var approvals = new ApprovalStore();
        var approvalPrompt = new TestApprovalPrompt(approvals);
        var paths = CipherVaultServicePaths.Create(
            Path.Combine(fixture.Root, "ProgramFiles"),
            Path.Combine(fixture.Root, "ProgramData"),
            Path.Combine(fixture.Root, "LocalAppData"));
        var transferPrompt = new TestTransferPrompt();
        var provider = new StrictAdminVaultRpcProvider(
            fixture.App.Services.GetRequiredService<HostVaultGateway>(),
            sessions,
            approvals,
            approvalPrompt,
            new TransferCoordinator(paths),
            transferPrompt);
        var now = DateTimeOffset.UtcNow;
        var session = new AdminSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new VerifiedProcessIdentity(
                VerifiedProcessKind.AdminUi,
                (uint)Environment.ProcessId,
                now.AddMinutes(-1),
                WindowsIdentity.GetCurrent().User!.Value,
                "S-1-5-5-0-1",
                1,
                Environment.ProcessPath!,
                new string('a', 64),
                SignerThumbprint: null,
                SignerSubject: null,
                IntegrityLevel: 0x2000),
            WindowsIdentity.GetCurrent().User!.Value,
            "S-1-5-80-1",
            now,
            now,
            now.AddMinutes(15),
            now.AddHours(4),
            VaultSessionId: null,
            State: AdminSessionState.Active,
            RevocationReason: null);

        var result = await provider.HandleAsync(
            session,
            "admin.notes.create",
            JsonSerializer.SerializeToElement(
                new HostCreateNoteRequest(
                    "Strict pipe note",
                    Folder: null,
                    Format: "md",
                    Content: "Created through the strict Admin pipe.",
                    Tags: ["strict"]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);

        var write = Assert.IsType<VaultWriteResult>(result);
        Assert.NotEqual(Guid.Empty, write.DocumentId);
        Assert.Equal(1, write.Revision);
        var appendResult = await provider.HandleAsync(
            session,
            "admin.notes.append",
            JsonSerializer.SerializeToElement(
                new AdminAppendNoteRequest(
                    write.DocumentId,
                    new HostAppendNoteRequest(
                        write.Revision,
                        "\nAppended through strict pipe.")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var appended = Assert.IsType<VaultWriteResult>(appendResult);
        Assert.Equal(write.DocumentId, appended.DocumentId);
        Assert.Equal(2, appended.Revision);
        var updateResult = await provider.HandleAsync(
            session,
            "admin.notes.update",
            JsonSerializer.SerializeToElement(
                new AdminUpdateNoteRequest(
                    appended.DocumentId,
                    new HostUpdateNoteRequest(
                        appended.Revision,
                        Title: "Updated strict pipe note",
                        Folder: null,
                        Content: "Updated through strict pipe.",
                        Tags: ["strict", "updated"])),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var updated = Assert.IsType<VaultWriteResult>(updateResult);
        Assert.Equal(appended.DocumentId, updated.DocumentId);
        Assert.Equal(3, updated.Revision);
        var readResult = await provider.HandleAsync(
            session,
            "admin.notes.read",
            JsonSerializer.SerializeToElement(
                new AdminReadNoteRequest(
                    updated.DocumentId,
                    Offset: 0,
                    MaxChars: 1000),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var read = Assert.IsType<VaultReadResult>(readResult);
        Assert.Equal("Updated strict pipe note", read.Title);
        Assert.Equal("Updated through strict pipe.", read.Content);
        var deleteResult = await provider.HandleAsync(
            session,
            "admin.notes.delete",
            JsonSerializer.SerializeToElement(
                new AdminTrashNoteRequest(
                    updated.DocumentId,
                    new HostTrashNoteRequest(updated.Revision)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var deleted = Assert.IsType<VaultDeleteResult>(deleteResult);
        Assert.Equal(updated.DocumentId, deleted.DocumentId);
        Assert.Equal(3, deleted.Revision);
        var catalogResult = await provider.HandleAsync(
            session,
            "admin.catalog.list",
            JsonSerializer.SerializeToElement(
                new AdminCatalogListRequest(
                    Folder: null,
                    Cursor: null,
                    Limit: 100),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var catalog = Assert.IsType<VaultCatalogPage>(catalogResult);
        Assert.Empty(catalog.Items);
        var trashResult = await provider.HandleAsync(
            session,
            "admin.trash.list",
            JsonSerializer.SerializeToElement(
                new AdminTrashListRequest(
                    Cursor: null,
                    Limit: 100),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var trash = Assert.IsType<VaultTrashPage>(trashResult);
        var trashed = Assert.Single(trash.Items);
        Assert.Equal(updated.DocumentId, trashed.DocumentId);
        var restoreResult = await provider.HandleAsync(
            session,
            "admin.trash.restore",
            JsonSerializer.SerializeToElement(
                new AdminRestoreTrashRequest(
                    trashed.DocumentId,
                    new HostRestoreTrashRequest(
                        trashed.Revision,
                        RenamePath: null)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var restored = Assert.IsType<VaultRestoreResult>(restoreResult);
        Assert.Equal(trashed.DocumentId, restored.DocumentId);
        Assert.EndsWith(".md", restored.VirtualPath, StringComparison.Ordinal);
        var revisionsResult = await provider.HandleAsync(
            session,
            "admin.revisions.list",
            JsonSerializer.SerializeToElement(
                new AdminRevisionListRequest(
                    restored.DocumentId,
                    Cursor: null,
                    Limit: 100),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var revisions = Assert.IsType<VaultRevisionPage>(revisionsResult);
        Assert.NotEmpty(revisions.Items);
        Assert.All(
            revisions.Items,
            revision => Assert.Equal(restored.DocumentId, revision.DocumentId));
        var revisionToRestore = revisions.Items[0];
        var restoreRevisionResult = await provider.HandleAsync(
            session,
            "admin.revisions.restore",
            JsonSerializer.SerializeToElement(
                new AdminRestoreRevisionRequest(
                    restored.DocumentId,
                    revisionToRestore.RevisionId,
                    new HostRestoreRevisionRequest(restored.Revision)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var restoredRevision = Assert.IsType<VaultWriteResult>(
            restoreRevisionResult);
        Assert.Equal(restored.DocumentId, restoredRevision.DocumentId);
        Assert.True(restoredRevision.Revision > restored.Revision);
        var verifyResult = await provider.HandleAsync(
            session,
            "admin.maintenance.verify",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);
        var verification = Assert.IsType<VaultIntegrityReport>(verifyResult);
        Assert.True(verification.Valid);
        var rebuildResult = await provider.HandleAsync(
            session,
            "admin.maintenance.rebuild_index",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);
        var rebuiltRows = Assert.IsType<int>(rebuildResult);
        Assert.True(rebuiltRows > 0);
        var auditResult = await provider.HandleAsync(
            session,
            "admin.maintenance.audit",
            JsonSerializer.SerializeToElement(
                new AdminAuditListRequest(
                    Cursor: null,
                    Limit: 100),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var audit = Assert.IsType<VaultAuditPage>(auditResult);
        Assert.NotEmpty(audit.Items);
        var compatibilityOnly = await Assert.ThrowsAsync<
            AdminPipeRpcException>(
            () => provider.HandleAsync(
                session,
                "admin.agent.clients.list",
                JsonSerializer.SerializeToElement(new { }),
                CancellationToken.None));
        Assert.Equal("compatibility_only", compatibilityOnly.Code);
        const string newPassword = "changed strict password";
        fixture.CredentialPrompt.EnqueueResult(
            new HostCredentialPromptResult(
                true,
                HostTestFixture.VaultPassword,
                VaultCredentialType.Password));
        fixture.CredentialPrompt.EnqueueResult(
            new HostCredentialPromptResult(
                true,
                newPassword,
                VaultCredentialType.Password));
        fixture.CredentialPrompt.EnqueueResult(
            new HostCredentialPromptResult(
                true,
                newPassword,
                VaultCredentialType.Password));
        var passwordChanged = await provider.HandleAsync(
            session,
            "admin.maintenance.change_password",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);
        Assert.Equal(true, passwordChanged);
        await sessions.LockAsync("test-password-change");
        fixture.CredentialPrompt.EnqueueResult(
            new HostCredentialPromptResult(
                true,
                newPassword,
                VaultCredentialType.Password));
        var unlocked = await sessions.UnlockAsync(
            HostCredentialPreference.Password);
        Assert.Equal(VaultSessionState.Unlocked, unlocked.State);
        fixture.CredentialPrompt.EnqueueResult(
            new HostCredentialPromptResult(
                true,
                newPassword,
                VaultCredentialType.Password));
        var rotationResult = await provider.HandleAsync(
            session,
            "admin.maintenance.rotate_recovery",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);
        var rotation = Assert.IsType<HostRecoveryRotationData>(rotationResult);
        Assert.False(string.IsNullOrWhiteSpace(rotation.RecoveryKey));
        fixture.CredentialPrompt.EnqueueResult(
            new HostCredentialPromptResult(
                true,
                rotation.RecoveryKey,
                VaultCredentialType.RecoveryKey));
        var recoveryConfirmed = await provider.HandleAsync(
            session,
            "admin.maintenance.confirm_recovery",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);
        Assert.Equal(true, recoveryConfirmed);
        var deleteBeforePurge = await provider.HandleAsync(
            session,
            "admin.notes.delete",
            JsonSerializer.SerializeToElement(
                new AdminTrashNoteRequest(
                    restored.DocumentId,
                    new HostTrashNoteRequest(
                        restoredRevision.Revision)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        _ = Assert.IsType<VaultDeleteResult>(deleteBeforePurge);
        var purgeResult = await provider.HandleAsync(
            session,
            "admin.trash.purge",
            JsonSerializer.SerializeToElement(
                new AdminPurgeNoteRequest(restored.DocumentId),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var purged = Assert.IsType<VaultPurgeResult>(purgeResult);
        Assert.True(purged.Purged);
        Assert.Equal(1, approvalPrompt.RequestCount);
        var backupPath = Path.Combine(
            fixture.Root,
            "exports",
            "vault.cvbackup");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        var backupResult = await provider.HandleAsync(
            session,
            "admin.maintenance.backup",
            JsonSerializer.SerializeToElement(
                new HostBackupRequest(backupPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var backup = Assert.IsType<VaultBackupResult>(backupResult);
        Assert.Equal(Path.GetFullPath(backupPath), backup.BackupPath);
        Assert.True(File.Exists(backupPath));
        Assert.Equal(2, approvalPrompt.RequestCount);
    }

    private sealed class TestApprovalPrompt(ApprovalStore store)
        : IApprovalPrompt
    {
        public int RequestCount { get; private set; }

        public Task<HostApprovalDecision> RequestAsync(
            HostApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(store.Decide(
                request,
                approved: true,
                brokerProcessSessionId: Guid.NewGuid()));
        }
    }

    private sealed class TestTransferPrompt : ITransferPrompt
    {
        public Task<HostTransferResult> TransferAsync(
            HostTransferRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Direction != HostTransferDirection.Export ||
                !File.Exists(request.StagingPath))
            {
                return Task.FromResult(new HostTransferResult(
                    request.TransferId,
                    false,
                    null,
                    "transfer_invalid"));
            }
            Directory.CreateDirectory(
                Path.GetDirectoryName(request.SuggestedPath)!);
            File.Copy(
                request.StagingPath,
                request.SuggestedPath,
                overwrite: false);
            return Task.FromResult(new HostTransferResult(
                request.TransferId,
                true,
                Path.GetFullPath(request.SuggestedPath),
                null));
        }
    }
}
