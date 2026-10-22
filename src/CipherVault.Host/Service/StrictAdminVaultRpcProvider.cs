using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Core.ImportExport;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using Microsoft.Extensions.Hosting;

namespace CipherVault.Host.Service;

public sealed class StrictAdminVaultRpcProvider(
    HostVaultGateway vault,
    HostSessionCoordinator sessions,
    ApprovalStore approvals,
    IApprovalPrompt approvalPrompt,
    TransferCoordinator transfers,
    ITransferPrompt transferPrompt) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, TransferWorkspace> _importWorkspaces =
        new();

    public async Task<object> HandleAsync(
        AdminSession session,
        string method,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "admin.catalog.list":
                var catalogRequest =
                    PipeRpcCodec.ReadPayload<AdminCatalogListRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                return await vault.ExecuteAsync(
                    (service, token) => service.ListCatalogAsync(
                        catalogRequest.Folder,
                        catalogRequest.Cursor,
                        catalogRequest.Limit,
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.search.notes":
                var searchRequest =
                    PipeRpcCodec.ReadPayload<AdminSearchNotesRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                return await vault.ExecuteAsync(
                    (service, token) => service.SearchNotesAsync(
                        searchRequest.Query,
                        searchRequest.Folder,
                        searchRequest.Cursor,
                        searchRequest.Limit,
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.notes.read":
                var readRequest = PipeRpcCodec.ReadPayload<AdminReadNoteRequest>(
                    new PipeRpcRequest(
                        PipeProtocol.Version,
                        Guid.Empty,
                        method,
                        payload));
                return await vault.ExecuteAsync(
                    (service, token) => service.ReadNoteAsync(
                        readRequest.DocumentId,
                        readRequest.Offset,
                        readRequest.MaxChars,
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.notes.scan":
                var scanRequest = PipeRpcCodec.ReadPayload<AdminScanNotesRequest>(
                    new PipeRpcRequest(
                        PipeProtocol.Version,
                        Guid.Empty,
                        method,
                        payload));
                return await vault.ExecuteAsync(
                    (service, token) => service.ScanNotesAsync(
                        scanRequest.Cursor,
                        scanRequest.Limit,
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.notes.create":
                var createRequest = PipeRpcCodec.ReadPayload<HostCreateNoteRequest>(
                    new PipeRpcRequest(
                        PipeProtocol.Version,
                        Guid.Empty,
                        method,
                        payload));
                if (string.IsNullOrWhiteSpace(createRequest.Title))
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Note title is required.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.CreateNoteAsync(
                        createRequest.Title,
                        createRequest.Folder,
                        createRequest.Format ?? "md",
                        createRequest.Content ?? string.Empty,
                        createRequest.Tags,
                        Actor(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.notes.append":
                var appendRequest =
                    PipeRpcCodec.ReadPayload<AdminAppendNoteRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                if (appendRequest.DocumentId == Guid.Empty ||
                    appendRequest.Request.ExpectedRevision < 1 ||
                    appendRequest.Request.Content is null)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Append request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.AppendNoteAsync(
                        appendRequest.DocumentId,
                        appendRequest.Request.ExpectedRevision,
                        appendRequest.Request.Content,
                        Actor(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.notes.update":
                var updateRequest =
                    PipeRpcCodec.ReadPayload<AdminUpdateNoteRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                if (updateRequest.DocumentId == Guid.Empty ||
                    updateRequest.Request.ExpectedRevision < 1)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Update request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.UpdateNoteAsync(
                        updateRequest.DocumentId,
                        updateRequest.Request.ExpectedRevision,
                        new VaultNotePatch(
                            updateRequest.Request.Title,
                            updateRequest.Request.Folder,
                            updateRequest.Request.Content,
                            updateRequest.Request.Tags),
                        Actor(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.notes.delete":
                var deleteRequest =
                    PipeRpcCodec.ReadPayload<AdminTrashNoteRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                if (deleteRequest.DocumentId == Guid.Empty ||
                    deleteRequest.Request.ExpectedRevision < 1)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Delete request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.DeleteNoteAsync(
                        deleteRequest.DocumentId,
                        deleteRequest.Request.ExpectedRevision,
                        Actor(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.trash.list":
                var trashRequest =
                    PipeRpcCodec.ReadPayload<AdminTrashListRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                return await vault.ExecuteAsync(
                    (service, token) => service.ListTrashAsync(
                        trashRequest.Cursor,
                        trashRequest.Limit,
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.trash.restore":
                var restoreRequest =
                    PipeRpcCodec.ReadPayload<AdminRestoreTrashRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                if (restoreRequest.DocumentId == Guid.Empty ||
                    restoreRequest.Request.ExpectedRevision < 1)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Restore request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.RestoreTrashAsync(
                        restoreRequest.DocumentId,
                        restoreRequest.Request.ExpectedRevision,
                        restoreRequest.Request.RenamePath,
                        Actor(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.revisions.list":
                var revisionRequest =
                    PipeRpcCodec.ReadPayload<AdminRevisionListRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                if (revisionRequest.DocumentId == Guid.Empty)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Revision list request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.ListRevisionsAsync(
                        revisionRequest.DocumentId,
                        revisionRequest.Cursor,
                        revisionRequest.Limit,
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.revisions.restore":
                var restoreRevisionRequest =
                    PipeRpcCodec.ReadPayload<AdminRestoreRevisionRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                if (restoreRevisionRequest.DocumentId == Guid.Empty ||
                    restoreRevisionRequest.RevisionId == Guid.Empty ||
                    restoreRevisionRequest.Request.ExpectedRevision < 1)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Revision restore request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.RestoreRevisionAsync(
                        restoreRevisionRequest.DocumentId,
                        restoreRevisionRequest.RevisionId,
                        restoreRevisionRequest.Request.ExpectedRevision,
                        Actor(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.maintenance.verify":
                return await vault.ExecuteAsync(
                    (service, token) => service.VerifyVaultAsync(token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.maintenance.rebuild_index":
                return await vault.ExecuteMaintenanceAsync(
                    (service, token) => service.RebuildSearchIndexAsync(
                        Actor(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.maintenance.audit":
                var auditRequest =
                    PipeRpcCodec.ReadPayload<AdminAuditListRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                return await vault.ExecuteAsync(
                    (service, token) => service.GetAuditPageAsync(
                        auditRequest.Cursor,
                        auditRequest.Limit,
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.agent.clients.list":
            case "admin.agent.clients.create":
            case "admin.agent.clients.rotate":
            case "admin.agent.clients.delete":
                throw new AdminPipeRpcException(
                    "compatibility_only",
                    "Agent Token management is unavailable in strict mode.");
            case "admin.maintenance.change_password":
                await sessions.ChangePasswordAsync(
                    Actor(session),
                    cancellationToken).ConfigureAwait(false);
                return true;
            case "admin.maintenance.rotate_recovery":
                var recoveryKey = await sessions.RotateRecoveryKeyAsync(
                    Actor(session),
                    cancellationToken).ConfigureAwait(false);
                return new HostRecoveryRotationData(recoveryKey);
            case "admin.maintenance.confirm_recovery":
                await sessions.ConfirmRecoveryKeyAsync(
                    Actor(session),
                    cancellationToken).ConfigureAwait(false);
                return true;
            case "admin.trash.purge":
                var purgeRequest = PipeRpcCodec.ReadPayload<AdminPurgeNoteRequest>(
                    new PipeRpcRequest(
                        PipeProtocol.Version,
                        Guid.Empty,
                        method,
                        payload));
                if (purgeRequest.DocumentId == Guid.Empty)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Purge request is invalid.");
                }
                var snapshot = await sessions.GetSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot.SessionId is null ||
                    snapshot.State is not (
                        VaultSessionState.Unlocked or
                        VaultSessionState.Expiring))
                {
                    throw new AdminPipeRpcException(
                        "session_locked",
                        "Vault must be unlocked for purge.");
                }
                var purgeParameters = JsonSerializer.SerializeToElement(
                    new { documentId = purgeRequest.DocumentId });
                var operation = approvals.Prepare(
                    ApprovalOperationKind.PurgeNote,
                    session.ProcessSessionId,
                    snapshot.SessionId,
                    purgeParameters);
                var request = approvals.CreateRequest(
                    operation,
                    "永久删除文档",
                    $"永久删除文档 {purgeRequest.DocumentId}。此操作无法撤销。");
                var decision = await approvalPrompt.RequestAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
                if (!decision.Approved)
                {
                    throw new AdminPipeRpcException(
                        "approval_denied",
                        decision.DenialReason ?? "User denied purge.");
                }
                var consumed = approvals.Consume(
                    operation.OperationId,
                    ApprovalOperationKind.PurgeNote,
                    session.ProcessSessionId,
                    snapshot.SessionId,
                    operation.ParametersHash);
                if (consumed.Status != ApprovalConsumeStatus.Consumed)
                {
                    throw new AdminPipeRpcException(
                        consumed.ReasonCode ?? "approval_invalid",
                        "Purge approval could not be consumed.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.PurgeNoteAsync(
                        purgeRequest.DocumentId,
                        Actor(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.maintenance.backup":
                {
                    var backupRequest = PipeRpcCodec.ReadPayload<HostBackupRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                    if (string.IsNullOrWhiteSpace(backupRequest.DestinationPath))
                    {
                        throw new AdminPipeRpcException(
                            "invalid_input",
                            "Backup destination hint is required.");
                    }
                    var backupSnapshot = await sessions
                        .GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (backupSnapshot.SessionId is null)
                    {
                        throw new AdminPipeRpcException(
                            "session_locked",
                            "Vault must be unlocked for backup.");
                    }
                    var backupParameters = JsonSerializer.SerializeToElement(
                        backupRequest);
                    var backupOperation = approvals.Prepare(
                        ApprovalOperationKind.CreateBackup,
                        session.ProcessSessionId,
                        backupSnapshot.SessionId,
                        backupParameters);
                    var backupApproval = approvals.CreateRequest(
                        backupOperation,
                        "创建加密备份",
                        $"创建 Vault 加密备份，建议保存到 {backupRequest.DestinationPath}。");
                    var backupDecision = await approvalPrompt.RequestAsync(
                        backupApproval,
                        cancellationToken).ConfigureAwait(false);
                    if (!backupDecision.Approved)
                    {
                        throw new AdminPipeRpcException(
                            "approval_denied",
                            backupDecision.DenialReason ?? "User denied backup.");
                    }
                    ConsumeApproval(
                        approvals,
                        backupOperation,
                        session,
                        backupSnapshot.SessionId.Value,
                        ApprovalOperationKind.CreateBackup);
                    using var backupWorkspace = transfers.CreateWorkspace(
                        HostTransferDirection.Export,
                        HostTransferPayloadKind.File,
                        session.Client.UserSid,
                        session.Client.LogonSid,
                        "vault.cvbackup");
                    var stagingBackup = await vault.ExecuteAsync(
                        (service, token) => service.CreateBackupAsync(
                            backupWorkspace.PayloadPath,
                            Actor(session),
                            token),
                        cancellationToken).ConfigureAwait(false);
                    transfers.FreezeManifest(backupWorkspace);
                    var backupTransfer = await transferPrompt.TransferAsync(
                        new HostTransferRequest(
                            backupWorkspace.TransferId,
                            HostTransferDirection.Export,
                            HostTransferPayloadKind.File,
                            backupWorkspace.PayloadPath,
                            backupRequest.DestinationPath,
                            backupOperation.ExpiresAt),
                        cancellationToken).ConfigureAwait(false);
                    if (!backupTransfer.Completed ||
                        string.IsNullOrWhiteSpace(backupTransfer.FinalPath))
                    {
                        throw new AdminPipeRpcException(
                            backupTransfer.ErrorCode ?? "transfer_failed",
                            "Backup file transfer did not complete.");
                    }
                    return new VaultBackupResult(
                        backupTransfer.FinalPath,
                        stagingBackup.VaultId,
                        stagingBackup.CreatedAt,
                        stagingBackup.BackupBytes,
                        stagingBackup.AuditRecorded);
                }
            case "admin.maintenance.export":
                {
                    var exportRequest = PipeRpcCodec.ReadPayload<HostExportRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                    if (string.IsNullOrWhiteSpace(exportRequest.DestinationPath))
                    {
                        throw new AdminPipeRpcException(
                            "invalid_input",
                            "Export destination hint is required.");
                    }
                    var exportSnapshot = await sessions
                        .GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (exportSnapshot.SessionId is null)
                    {
                        throw new AdminPipeRpcException(
                            "session_locked",
                            "Vault must be unlocked for export.");
                    }
                    var exportOperation = approvals.Prepare(
                        ApprovalOperationKind.ExportPlaintext,
                        session.ProcessSessionId,
                        exportSnapshot.SessionId,
                        JsonSerializer.SerializeToElement(exportRequest));
                    var exportApproval = approvals.CreateRequest(
                        exportOperation,
                        "导出明文资料库",
                        $"导出资料库明文内容，建议保存到 {exportRequest.DestinationPath}。");
                    var exportDecision = await approvalPrompt.RequestAsync(
                        exportApproval,
                        cancellationToken).ConfigureAwait(false);
                    if (!exportDecision.Approved)
                    {
                        throw new AdminPipeRpcException(
                            "approval_denied",
                            exportDecision.DenialReason ?? "User denied export.");
                    }
                    ConsumeApproval(
                        approvals,
                        exportOperation,
                        session,
                        exportSnapshot.SessionId.Value,
                        ApprovalOperationKind.ExportPlaintext);
                    using var exportWorkspace = transfers.CreateWorkspace(
                        HostTransferDirection.Export,
                        HostTransferPayloadKind.Directory,
                        session.Client.UserSid,
                        session.Client.LogonSid,
                        "export");
                    var stagingExport = await vault.ExecuteMaintenanceAsync(
                        (service, token) => service.ExportSnapshotAsync(
                            exportWorkspace.PayloadPath,
                            Actor(session),
                            token),
                        cancellationToken).ConfigureAwait(false);
                    transfers.FreezeManifest(exportWorkspace);
                    var exportTransfer = await transferPrompt.TransferAsync(
                        new HostTransferRequest(
                            exportWorkspace.TransferId,
                            HostTransferDirection.Export,
                            HostTransferPayloadKind.Directory,
                            exportWorkspace.PayloadPath,
                            exportRequest.DestinationPath,
                            exportOperation.ExpiresAt),
                        cancellationToken).ConfigureAwait(false);
                    if (!exportTransfer.Completed ||
                        string.IsNullOrWhiteSpace(exportTransfer.FinalPath))
                    {
                        throw new AdminPipeRpcException(
                            exportTransfer.ErrorCode ?? "transfer_failed",
                            "Export file transfer did not complete.");
                    }
                    var manifestRelative = Path.GetRelativePath(
                        stagingExport.Destination,
                        stagingExport.ManifestPath);
                    return new VaultExportResult(
                        exportTransfer.FinalPath,
                        stagingExport.ExportedDocuments,
                        Path.Combine(exportTransfer.FinalPath, manifestRelative),
                        stagingExport.AuditRecorded);
                }
            case "admin.maintenance.import_preview":
                {
                    var importRequest = PipeRpcCodec.ReadPayload<HostImportPreviewRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                    if (string.IsNullOrWhiteSpace(importRequest.SourceRoot))
                    {
                        throw new AdminPipeRpcException(
                            "invalid_input",
                            "Import source hint is required.");
                    }
                    var importSnapshot = await sessions
                        .GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (importSnapshot.SessionId is null)
                    {
                        throw new AdminPipeRpcException(
                            "session_locked",
                            "Vault must be unlocked for import.");
                    }
                    var importOperation = approvals.Prepare(
                        ApprovalOperationKind.PreviewImport,
                        session.ProcessSessionId,
                        importSnapshot.SessionId,
                        JsonSerializer.SerializeToElement(importRequest));
                    var importApproval = approvals.CreateRequest(
                        importOperation,
                        "预览导入资料库",
                        $"选择要导入的文件夹，建议位置 {importRequest.SourceRoot}。");
                    var importDecision = await approvalPrompt.RequestAsync(
                        importApproval,
                        cancellationToken).ConfigureAwait(false);
                    if (!importDecision.Approved)
                    {
                        throw new AdminPipeRpcException(
                            "approval_denied",
                            importDecision.DenialReason ?? "User denied import.");
                    }
                    ConsumeApproval(
                        approvals,
                        importOperation,
                        session,
                        importSnapshot.SessionId.Value,
                        ApprovalOperationKind.PreviewImport);
                    var workspace = transfers.CreateWorkspace(
                        HostTransferDirection.Import,
                        HostTransferPayloadKind.Directory,
                        session.Client.UserSid,
                        session.Client.LogonSid,
                        "input");
                    try
                    {
                        var transfer = await transferPrompt.TransferAsync(
                            new HostTransferRequest(
                                workspace.TransferId,
                                HostTransferDirection.Import,
                                HostTransferPayloadKind.Directory,
                                workspace.PayloadPath,
                                importRequest.SourceRoot,
                                importOperation.ExpiresAt),
                            cancellationToken).ConfigureAwait(false);
                        if (!transfer.Completed)
                        {
                            throw new AdminPipeRpcException(
                                transfer.ErrorCode ?? "transfer_failed",
                                "Import file transfer did not complete.");
                        }
                        transfers.EnsureNoReparsePoints(workspace.PayloadPath);
                        transfers.FreezeManifest(workspace);
                        var preview = await vault.ExecuteAsync(
                            (service, token) => service.PreviewImportAsync(
                                workspace.PayloadPath,
                                Actor(session),
                                token),
                            cancellationToken).ConfigureAwait(false);
                        if (!_importWorkspaces.TryAdd(preview.JobId, workspace))
                        {
                            throw new AdminPipeRpcException(
                                "conflict",
                                "Import job already exists.");
                        }
                        workspace = null!;
                        return preview;
                    }
                    finally
                    {
                        workspace?.Dispose();
                    }
                }
            case "admin.maintenance.import_commit":
                {
                    var commitRequest = PipeRpcCodec.ReadPayload<AdminImportCommitRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                    if (!_importWorkspaces.TryGetValue(
                            commitRequest.JobId,
                            out var importWorkspace))
                    {
                        throw new AdminPipeRpcException(
                            "not_found",
                            "Import job was not found.");
                    }
                    var commitSnapshot = await sessions
                        .GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (commitSnapshot.SessionId is null)
                    {
                        throw new AdminPipeRpcException(
                            "session_locked",
                            "Vault must be unlocked for import commit.");
                    }
                    var commitOperation = approvals.Prepare(
                        ApprovalOperationKind.CommitImport,
                        session.ProcessSessionId,
                        commitSnapshot.SessionId,
                        JsonSerializer.SerializeToElement(commitRequest));
                    var commitApproval = approvals.CreateRequest(
                        commitOperation,
                        "提交资料库导入",
                        $"提交已预览的导入任务 {commitRequest.JobId}。");
                    var commitDecision = await approvalPrompt.RequestAsync(
                        commitApproval,
                        cancellationToken).ConfigureAwait(false);
                    if (!commitDecision.Approved)
                    {
                        throw new AdminPipeRpcException(
                            "approval_denied",
                            commitDecision.DenialReason ?? "User denied import commit.");
                    }
                    ConsumeApproval(
                        approvals,
                        commitOperation,
                        session,
                        commitSnapshot.SessionId.Value,
                        ApprovalOperationKind.CommitImport);
                    if (!_importWorkspaces.TryRemove(
                            commitRequest.JobId,
                            out importWorkspace))
                    {
                        throw new AdminPipeRpcException(
                            "not_found",
                            "Import job was not found.");
                    }
                    using (importWorkspace)
                    {
                        return await vault.ExecuteMaintenanceAsync(
                            (service, token) => service.CommitImportAsync(
                                commitRequest.JobId,
                                Actor(session),
                                token),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            case "admin.maintenance.restore_new":
                {
                    var restoreBackupRequest =
                        PipeRpcCodec.ReadPayload<HostRestoreNewRequest>(
                            new PipeRpcRequest(
                                PipeProtocol.Version,
                                Guid.Empty,
                                method,
                                payload));
                    if (string.IsNullOrWhiteSpace(restoreBackupRequest.BackupPath) ||
                        string.IsNullOrWhiteSpace(restoreBackupRequest.TargetPath))
                    {
                        throw new AdminPipeRpcException(
                            "invalid_input",
                            "Backup and restore target hints are required.");
                    }
                    var restoreSnapshot = await sessions
                        .GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (restoreSnapshot.SessionId is null)
                    {
                        throw new AdminPipeRpcException(
                            "session_locked",
                            "Vault must be unlocked for restore.");
                    }
                    var restoreOperation = approvals.Prepare(
                        ApprovalOperationKind.RestoreBackup,
                        session.ProcessSessionId,
                        restoreSnapshot.SessionId,
                        JsonSerializer.SerializeToElement(restoreBackupRequest));
                    var restoreApproval = approvals.CreateRequest(
                        restoreOperation,
                        "恢复 Vault 备份",
                        $"从备份恢复到新资料库目录，建议保存到 {restoreBackupRequest.TargetPath}。");
                    var restoreDecision = await approvalPrompt.RequestAsync(
                        restoreApproval,
                        cancellationToken).ConfigureAwait(false);
                    if (!restoreDecision.Approved)
                    {
                        throw new AdminPipeRpcException(
                            "approval_denied",
                            restoreDecision.DenialReason ?? "User denied restore.");
                    }
                    ConsumeApproval(
                        approvals,
                        restoreOperation,
                        session,
                        restoreSnapshot.SessionId.Value,
                        ApprovalOperationKind.RestoreBackup);
                    using var restoreWorkspace = transfers.CreateWorkspace(
                        HostTransferDirection.Import,
                        HostTransferPayloadKind.File,
                        session.Client.UserSid,
                        session.Client.LogonSid,
                        "vault.cvbackup");
                    var importTransfer = await transferPrompt.TransferAsync(
                        new HostTransferRequest(
                            restoreWorkspace.TransferId,
                            HostTransferDirection.Import,
                            HostTransferPayloadKind.File,
                            restoreWorkspace.PayloadPath,
                            restoreBackupRequest.BackupPath,
                            restoreOperation.ExpiresAt),
                        cancellationToken).ConfigureAwait(false);
                    if (!importTransfer.Completed)
                    {
                        throw new AdminPipeRpcException(
                            importTransfer.ErrorCode ?? "transfer_failed",
                            "Backup import into staging did not complete.");
                    }
                    transfers.EnsureNoReparsePoints(restoreWorkspace.PayloadPath);
                    transfers.FreezeManifest(restoreWorkspace);
                    var restoredPath = Path.Combine(
                        restoreWorkspace.RootPath,
                        "restored");
                    var restored = await sessions.RestoreBackupToNewAsync(
                        restoreWorkspace.PayloadPath,
                        restoredPath,
                        Actor(session),
                        cancellationToken).ConfigureAwait(false);
                    var exportTransfer = await transferPrompt.TransferAsync(
                        new HostTransferRequest(
                            restoreWorkspace.TransferId,
                            HostTransferDirection.Export,
                            HostTransferPayloadKind.Directory,
                            restoredPath,
                            restoreBackupRequest.TargetPath,
                            restoreOperation.ExpiresAt),
                        cancellationToken).ConfigureAwait(false);
                    if (!exportTransfer.Completed ||
                        string.IsNullOrWhiteSpace(exportTransfer.FinalPath))
                    {
                        throw new AdminPipeRpcException(
                            exportTransfer.ErrorCode ?? "transfer_failed",
                            "Restored Vault export did not complete.");
                    }
                    return new VaultRestoreBackupResult(
                        exportTransfer.FinalPath,
                        restored.VaultId,
                        restored.AuditRecorded);
                }
            case "admin.service.stop":
                {
                    var stopRequest = PipeRpcCodec.ReadPayload<HostStopRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                    var reason = string.IsNullOrWhiteSpace(stopRequest.Reason)
                        ? "user-requested"
                        : stopRequest.Reason.Trim();
                    if (reason.Length > 200)
                    {
                        throw new AdminPipeRpcException(
                            "invalid_input",
                            "Stop reason is too long.");
                    }
                    var locked = await sessions.LockAsync(
                        reason,
                        cancellationToken).ConfigureAwait(false);
                    if (locked.State != VaultSessionState.Locked)
                    {
                        throw new AdminPipeRpcException(
                            "lock_in_progress",
                            "Vault lock is still in progress.");
                    }
                    return true;
                }
            default:
                throw new AdminPipeRpcException(
                    "unsupported_method",
                    $"Unsupported Admin method: {method}");
        }
    }

    private static VaultActorKind Actor(AdminSession session)
    {
        return session.Client.Kind switch
        {
            VerifiedProcessKind.AdminUi => VaultActorKind.UserUi,
            VerifiedProcessKind.McpBridge => VaultActorKind.Mcp,
            _ => VaultActorKind.UserCli
        };
    }

    private static void ConsumeApproval(
        ApprovalStore approvals,
        PreparedOperation operation,
        AdminSession session,
        Guid vaultSessionId,
        ApprovalOperationKind kind)
    {
        var consumed = approvals.Consume(
            operation.OperationId,
            kind,
            session.ProcessSessionId,
            vaultSessionId,
            operation.ParametersHash);
        if (consumed.Status != ApprovalConsumeStatus.Consumed)
        {
            throw new AdminPipeRpcException(
                consumed.ReasonCode ?? "approval_invalid",
                "Operation approval could not be consumed.");
        }
    }

    public void Dispose()
    {
        foreach (var workspace in _importWorkspaces.Values)
        {
            workspace.Dispose();
        }
        _importWorkspaces.Clear();
    }
}
