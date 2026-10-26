using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using CipherVault.Core.Backup;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.ImportExport;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Service;
using CipherVault.Host.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CipherVault.Host.Tests;

public sealed class StrictAdminVaultRpcProviderTests
{
    [Fact]
    public async Task StopLocksVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var sessions = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        await sessions.UnlockAsync(HostCredentialPreference.Password);
        var paths = CipherVaultServicePaths.Create(
            Path.Combine(fixture.Root, "ProgramFiles"),
            Path.Combine(fixture.Root, "ProgramData"),
            Path.Combine(fixture.Root, "LocalAppData"));
        var provider = new StrictAdminVaultRpcProvider(
            fixture.App.Services.GetRequiredService<HostVaultGateway>(),
            sessions,
            new ApprovalStore(),
            new TestApprovalPrompt(new ApprovalStore()),
            new TransferCoordinator(paths),
            new TestTransferPrompt());
        var session = CreateAdminSession();

        var result = await provider.HandleAsync(
            session,
            "admin.service.stop",
            JsonSerializer.SerializeToElement(
                new HostStopRequest("test-stop"),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);

        Assert.Equal(true, result);
        Assert.Equal(
            VaultSessionState.Locked,
            (await sessions.GetSnapshotAsync()).State);
    }

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
                    DocumentId: null,
                    Action: null,
                    CorrelationId: null,
                    Cursor: null,
                    Limit: 100),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var audit = Assert.IsType<VaultAuditPage>(auditResult);
        Assert.NotEmpty(audit.Items);
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
        var exportPath = Path.Combine(
            fixture.Root,
            "exports",
            "plaintext");
        var exportResult = await provider.HandleAsync(
            session,
            "admin.maintenance.export",
            JsonSerializer.SerializeToElement(
                new HostExportRequest(exportPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var export = Assert.IsType<VaultExportResult>(exportResult);
        Assert.Equal(Path.GetFullPath(exportPath), export.Destination);
        Assert.True(Directory.Exists(export.Destination));
        Assert.True(File.Exists(export.ManifestPath));
        Assert.Equal(3, approvalPrompt.RequestCount);
        var importSource = Path.Combine(fixture.Root, "import-source");
        Directory.CreateDirectory(importSource);
        File.WriteAllText(
            Path.Combine(importSource, "imported.md"),
            "# Imported\n\nImported through strict staging.");
        var previewResult = await provider.HandleAsync(
            session,
            "admin.maintenance.import_preview",
            JsonSerializer.SerializeToElement(
                new HostImportPreviewRequest(importSource),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var preview = Assert.IsType<VaultImportPreview>(previewResult);
        Assert.Equal(1, preview.SupportedFiles);
        Assert.Equal(4, approvalPrompt.RequestCount);
        var commitResult = await provider.HandleAsync(
            session,
            "admin.maintenance.import_commit",
            JsonSerializer.SerializeToElement(
                new AdminImportCommitRequest(preview.JobId),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var imported = Assert.IsType<VaultImportResult>(commitResult);
        Assert.Equal(1, imported.Imported);
        Assert.Equal(5, approvalPrompt.RequestCount);
        var restorePath = Path.Combine(
            fixture.Root,
            "restored-vault");
        fixture.CredentialPrompt.EnqueueResult(
            new HostCredentialPromptResult(
                true,
                newPassword,
                VaultCredentialType.Password));
        var backupRestoreResult = await provider.HandleAsync(
            session,
            "admin.maintenance.restore_new",
            JsonSerializer.SerializeToElement(
                new HostRestoreNewRequest(backupPath, restorePath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var restoredVault = Assert.IsType<VaultRestoreBackupResult>(
            backupRestoreResult);
        Assert.Equal(Path.GetFullPath(restorePath), restoredVault.VaultPath);
        Assert.True(Directory.Exists(restorePath));
        Assert.Equal(6, approvalPrompt.RequestCount);
    }

    [Fact]
    public async Task ScopedAdminSessionFiltersCatalogBeforePagination()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var sessions = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        await sessions.UnlockAsync(HostCredentialPreference.Password);
        var paths = CipherVaultServicePaths.Create(
            Path.Combine(fixture.Root, "ProgramFiles"),
            Path.Combine(fixture.Root, "ProgramData"),
            Path.Combine(fixture.Root, "LocalAppData"));
        var provider = new StrictAdminVaultRpcProvider(
            fixture.App.Services.GetRequiredService<HostVaultGateway>(),
            sessions,
            new ApprovalStore(),
            new TestApprovalPrompt(new ApprovalStore()),
            new TransferCoordinator(paths),
            new TestTransferPrompt());
        var unscoped = CreateAdminSession();
        await provider.HandleAsync(
            unscoped,
            "admin.notes.create",
            JsonSerializer.SerializeToElement(
                new HostCreateNoteRequest(
                    "visible",
                    "Projects",
                    "md",
                    "visible content",
                    []),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        await provider.HandleAsync(
            unscoped,
            "admin.notes.create",
            JsonSerializer.SerializeToElement(
                new HostCreateNoteRequest(
                    "visible-two",
                    "Projects",
                    "md",
                    "visible content two",
                    []),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var hidden = Assert.IsType<VaultWriteResult>(await provider.HandleAsync(
            unscoped,
            "admin.notes.create",
            JsonSerializer.SerializeToElement(
                new HostCreateNoteRequest(
                    "hidden",
                    "Private",
                    "md",
                    "hidden content",
                    []),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None));
        Assert.True(AccessGrantPathPolicy.TryParse(
            ["read:Projects"],
            out _,
            out var pathPolicy));
        var scoped = CreateAdminSession(pathPolicy);

        var result = Assert.IsType<VaultCatalogPage>(await provider.HandleAsync(
            scoped,
            "admin.catalog.list",
            JsonSerializer.SerializeToElement(
                new AdminCatalogListRequest(null, null, 1),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None));

        var item = Assert.Single(result.Items);
        Assert.StartsWith("Projects/", item.VirtualPath);
        Assert.NotEqual(hidden.DocumentId, item.DocumentId);
        Assert.NotNull(result.NextCursor);
        Assert.DoesNotContain(
            hidden.DocumentId.ToString("D"),
            result.NextCursor!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportPreviewRejectsJunctionInStaging()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var sessions = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        await sessions.UnlockAsync(HostCredentialPreference.Password);
        var approvals = new ApprovalStore();
        var paths = CipherVaultServicePaths.Create(
            Path.Combine(fixture.Root, "ProgramFiles"),
            Path.Combine(fixture.Root, "ProgramData"),
            Path.Combine(fixture.Root, "LocalAppData"));
        using var transfers = new TransferCoordinator(paths);
        var source = Path.Combine(fixture.Root, "import-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "imported.md"),
            "# Imported\n\nJunction staging.");
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "must survive");
        var transferPrompt = new TestTransferPrompt
        {
            AfterImportDirectoryCopy = stagingPath => CreateJunction(
                Path.Combine(stagingPath, "escape"),
                outside)
        };
        using var provider = new StrictAdminVaultRpcProvider(
            fixture.App.Services.GetRequiredService<HostVaultGateway>(),
            sessions,
            approvals,
            new TestApprovalPrompt(approvals),
            transfers,
            transferPrompt);
        var session = CreateAdminSession();

        var exception = await Assert.ThrowsAsync<AdminPipeRpcException>(() =>
            provider.HandleAsync(
                session,
                "admin.maintenance.import_preview",
                JsonSerializer.SerializeToElement(
                    new HostImportPreviewRequest(source),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                CancellationToken.None));

        Assert.Equal("transfer_invalid", exception.Code);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task ImportCommitAfterWorkspaceExpiryReturnsNotFound()
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
        using var transfers = new TransferCoordinator(paths);
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var registry = new ImportWorkspaceRegistry(
            time,
            TimeSpan.FromHours(1));
        using var provider = new StrictAdminVaultRpcProvider(
            fixture.App.Services.GetRequiredService<HostVaultGateway>(),
            sessions,
            approvals,
            approvalPrompt,
            transfers,
            new TestTransferPrompt(),
            registry);
        var session = CreateAdminSession();
        var source = Path.Combine(fixture.Root, "import-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "imported.md"),
            "# Imported\n\nExpired staging.");
        var previewResult = await provider.HandleAsync(
            session,
            "admin.maintenance.import_preview",
            JsonSerializer.SerializeToElement(
                new HostImportPreviewRequest(source),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var preview = Assert.IsType<VaultImportPreview>(previewResult);
        Assert.Equal(1, registry.Count);

        time.Advance(TimeSpan.FromHours(1));

        var exception = await Assert.ThrowsAsync<AdminPipeRpcException>(() =>
            provider.HandleAsync(
                session,
                "admin.maintenance.import_commit",
                JsonSerializer.SerializeToElement(
                    new AdminImportCommitRequest(preview.JobId),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                CancellationToken.None));

        Assert.Equal("not_found", exception.Code);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task SecurityDiagnosticsReportsInstallConsistency()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var sessions = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        await sessions.UnlockAsync(HostCredentialPreference.Password);
        var paths = CipherVaultServicePaths.Create(
            Path.Combine(fixture.Root, "ProgramFiles"),
            Path.Combine(fixture.Root, "ProgramData"),
            Path.Combine(fixture.Root, "LocalAppData"));
        var configuration = CreateServiceConfiguration(
            paths,
            WindowsIdentity.GetCurrent().User!.Value);
        var installId = Guid.NewGuid();
        WriteInstallManifest(paths, configuration, installId);
        using var transfers = new TransferCoordinator(paths);
        using var provider = CreateProvider(
            fixture,
            sessions,
            transfers,
            configuration,
            paths);
        var session = CreateAdminSession();

        var result = await provider.HandleAsync(
            session,
            "admin.security.diagnostics",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);

        var diagnostics = Assert.IsType<AdminSecurityDiagnosticsData>(result);
        Assert.Equal("strict", diagnostics.SecurityMode);
        Assert.Equal(configuration.OwnerSid, diagnostics.OwnerSid);
        Assert.Equal("S-1-5-80-1", diagnostics.ServiceSid);
        Assert.Equal(1u, diagnostics.OwnerSessionId);
        Assert.False(diagnostics.BrokerAvailable);
        Assert.Equal(VaultSessionState.Unlocked.ToString(), diagnostics.VaultState);
        Assert.NotNull(diagnostics.VaultSessionId);
        Assert.True(diagnostics.Install.ManifestLoaded);
        Assert.Null(diagnostics.Install.ErrorCode);
        Assert.Equal(installId, diagnostics.Install.InstallId);
        Assert.True(diagnostics.Install.OwnerSidMatches);
        Assert.True(diagnostics.Install.ServiceAccountMatches);
        Assert.True(diagnostics.Install.PipesMatch);
        var json = JsonSerializer.Serialize(
            diagnostics,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recovery", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecurityDiagnosticsReportsMissingManifest()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var sessions = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        var paths = CipherVaultServicePaths.Create(
            Path.Combine(fixture.Root, "ProgramFiles"),
            Path.Combine(fixture.Root, "ProgramData"),
            Path.Combine(fixture.Root, "LocalAppData"));
        var configuration = CreateServiceConfiguration(
            paths,
            WindowsIdentity.GetCurrent().User!.Value);
        using var transfers = new TransferCoordinator(paths);
        using var provider = CreateProvider(
            fixture,
            sessions,
            transfers,
            configuration,
            paths);

        var result = await provider.HandleAsync(
            CreateAdminSession(),
            "admin.security.diagnostics",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);

        var diagnostics = Assert.IsType<AdminSecurityDiagnosticsData>(result);
        Assert.Null(diagnostics.ServiceSid);
        Assert.False(diagnostics.Install.ManifestLoaded);
        Assert.Equal("not_found", diagnostics.Install.ErrorCode);
        Assert.Null(diagnostics.Install.InstallId);
        Assert.False(diagnostics.Install.OwnerSidMatches);
        Assert.False(diagnostics.Install.ServiceAccountMatches);
        Assert.False(diagnostics.Install.PipesMatch);
    }

    [Fact]
    public async Task BridgesListReturnsConfiguredPolicyWithoutSecrets()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var sessions = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        var paths = CipherVaultServicePaths.Create(
            Path.Combine(fixture.Root, "ProgramFiles"),
            Path.Combine(fixture.Root, "ProgramData"),
            Path.Combine(fixture.Root, "LocalAppData"));
        var configuration = CreateServiceConfiguration(
            paths,
            WindowsIdentity.GetCurrent().User!.Value) with
        {
            Agent = new CipherVaultAgentRuntimeConfigFile
            {
                StrictTransport = "stdio-bridge",
                Bridges =
                [
                    new CipherVaultStrictBridgeConfigFile
                    {
                        Id = "mcp",
                        AccessMode = "readWrite",
                        Enabled = true,
                        Image = CreateImage(paths, "mcp")
                    },
                    new CipherVaultStrictBridgeConfigFile
                    {
                        Id = "disabled",
                        AccessMode = "readOnly",
                        Enabled = false,
                        Image = CreateImage(paths, "disabled")
                    }
                ]
            }
        };
        using var transfers = new TransferCoordinator(paths);
        using var provider = CreateProvider(
            fixture,
            sessions,
            transfers,
            configuration,
            paths);

        var result = await provider.HandleAsync(
            CreateAdminSession(),
            "admin.agent.bridges.list",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);

        var bridges = Assert.IsAssignableFrom<IReadOnlyList<AdminStrictBridgeData>>(
            result);
        Assert.Equal(2, bridges.Count);
        Assert.Equal("mcp", bridges[0].Id);
        Assert.Equal("readWrite", bridges[0].AccessMode);
        Assert.True(bridges[0].Enabled);
        Assert.Equal("disabled", bridges[1].Id);
        Assert.Equal("readOnly", bridges[1].AccessMode);
        Assert.False(bridges[1].Enabled);
        var json = JsonSerializer.Serialize(
            bridges,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant", json, StringComparison.OrdinalIgnoreCase);
    }

    private static StrictAdminVaultRpcProvider CreateProvider(
        HostTestFixture fixture,
        HostSessionCoordinator sessions,
        TransferCoordinator transfers,
        CipherVaultServiceConfigFile configuration,
        CipherVaultServicePaths paths)
    {
        return new StrictAdminVaultRpcProvider(
            fixture.App.Services.GetRequiredService<HostVaultGateway>(),
            sessions,
            new ApprovalStore(),
            new TestApprovalPrompt(new ApprovalStore()),
            transfers,
            new TestTransferPrompt(),
            importWorkspaces: null,
            configuration,
            paths,
            new BrokerConnectionRegistry());
    }

    private static CipherVaultServiceConfigFile CreateServiceConfiguration(
        CipherVaultServicePaths paths,
        string ownerSid)
    {
        return new CipherVaultServiceConfigFile
        {
            ConfigVersion = CipherVaultServiceConfiguration.CurrentConfigVersion,
            SecurityMode = CipherVaultServiceConfiguration.StrictModeValue,
            OwnerSid = ownerSid,
            ServiceAccount = "NT SERVICE\\CipherVaultHost",
            DataRoot = paths.DataRoot,
            VaultPath = paths.VaultPath,
            Pipes = new CipherVaultPipeConfigFile
            {
                Admin = "CipherVault.Admin.v1",
                Broker = "CipherVault.Broker.v1",
                Bridge = "CipherVault.Bridge.v1",
                RejectRemoteClients = true
            }
        };
    }

    private static CipherVaultImagePolicyFile CreateImage(
        CipherVaultServicePaths paths,
        string name)
    {
        return new CipherVaultImagePolicyFile
        {
            Kind = "mcpBridge",
            Path = Path.Combine(paths.InstallRoot, $"{name}.exe"),
            Sha256 = new string('b', 64),
            SignerThumbprint = new string('c', 64),
            SignerSubject = "CN=CipherVault Test"
        };
    }

    private static void WriteInstallManifest(
        CipherVaultServicePaths paths,
        CipherVaultServiceConfigFile configuration,
        Guid installId)
    {
        Directory.CreateDirectory(paths.DataRoot);
        var manifest = new CipherVaultInstallPublicManifest
        {
            Format = CipherVaultServiceConfiguration.InstallManifestFormat,
            FormatVersion =
                CipherVaultServiceConfiguration.InstallManifestFormatVersion,
            InstallId = installId,
            ProductVersion = "1.0.0",
            ServiceName = "CipherVaultHost",
            ServiceAccount = configuration.ServiceAccount,
            ServiceSid = "S-1-5-80-1",
            OwnerSid = configuration.OwnerSid,
            Pipes = configuration.Pipes,
            AllowedServerImages =
            [
                new CipherVaultImagePolicyFile
                {
                    Kind = "host",
                    Path = Path.Combine(
                        paths.InstallRoot,
                        "CipherVault.Host.exe"),
                    Sha256 = new string('a', 64),
                    SignerThumbprint = new string('a', 64),
                    SignerSubject = "CN=CipherVault Test"
                }
            ]
        };
        File.WriteAllText(
            paths.PublicManifestPath,
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
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

    private static AdminSession CreateAdminSession(
        AccessGrantPathPolicy? pathPolicy = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AdminSession(
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
            RevocationReason: null,
            PathPolicy: pathPolicy);
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cmd.exe could not be started.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Junction creation failed: {standardOutput}{standardError}");
        }
    }

    private sealed class TestTransferPrompt : ITransferPrompt
    {
        public Action<string>? AfterImportDirectoryCopy { get; init; }

        public Task<HostTransferResult> TransferAsync(
            HostTransferRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Direction == HostTransferDirection.Import &&
                request.PayloadKind == HostTransferPayloadKind.Directory)
            {
                CopyDirectory(request.SuggestedPath, request.StagingPath);
                AfterImportDirectoryCopy?.Invoke(request.StagingPath);
                return Task.FromResult(new HostTransferResult(
                    request.TransferId,
                    true,
                    Path.GetFullPath(request.StagingPath),
                    null));
            }
            if (request.Direction == HostTransferDirection.Import &&
                request.PayloadKind == HostTransferPayloadKind.File)
            {
                if (!File.Exists(request.SuggestedPath))
                {
                    return Task.FromResult(new HostTransferResult(
                        request.TransferId,
                        false,
                        null,
                        "transfer_invalid"));
                }
                File.Copy(
                    request.SuggestedPath,
                    request.StagingPath,
                    overwrite: false);
                return Task.FromResult(new HostTransferResult(
                    request.TransferId,
                    true,
                    Path.GetFullPath(request.StagingPath),
                    null));
            }
            if (request.Direction != HostTransferDirection.Export)
            {
                return Task.FromResult(new HostTransferResult(
                    request.TransferId,
                    false,
                    null,
                    "transfer_invalid"));
            }
            if (request.PayloadKind == HostTransferPayloadKind.File)
            {
                if (!File.Exists(request.StagingPath))
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
            }
            else
            {
                CopyDirectory(request.StagingPath, request.SuggestedPath);
            }
            return Task.FromResult(new HostTransferResult(
                request.TransferId,
                true,
                Path.GetFullPath(request.SuggestedPath),
                null));
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(
                         source,
                         "*",
                         SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(
                    destination,
                    Path.GetRelativePath(source, directory)));
            }
            foreach (var file in Directory.EnumerateFiles(
                         source,
                         "*",
                         SearchOption.AllDirectories))
            {
                var target = Path.Combine(
                    destination,
                    Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
        }
    }
}
