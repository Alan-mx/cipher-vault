using System.IO;
using System.Text.Json;
using CipherVault.Core.Backup;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.ImportExport;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Mcp;
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
    ITransferPrompt transferPrompt,
    ImportWorkspaceRegistry? importWorkspaces = null,
    CipherVaultServiceConfigFile? configuration = null,
    CipherVaultServicePaths? paths = null,
    BrokerConnectionRegistry? brokerConnections = null) : IDisposable
{
    private readonly ImportWorkspaceRegistry _importWorkspaces =
        importWorkspaces ?? new ImportWorkspaceRegistry();

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
                        ReadScope(session),
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
                        new VaultSearchFilter(
                            searchRequest.Tags,
                            searchRequest.Format,
                            searchRequest.UpdatedAfter,
                            searchRequest.UpdatedBefore,
                            searchRequest.HasBacklinks,
                            searchRequest.OrphanOnly),
                        ReadScope(session),
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
                        ReadScope(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.notes.map":
                var mapRequest = PipeRpcCodec.ReadPayload<AdminDocumentMapRequest>(
                    new PipeRpcRequest(
                        PipeProtocol.Version,
                        Guid.Empty,
                        method,
                        payload));
                if (mapRequest.DocumentId == Guid.Empty ||
                    !BridgeDocumentMapLimiter.IsValidLimit(
                        mapRequest.MaxHeadings) ||
                    !BridgeDocumentMapLimiter.IsValidLimit(
                        mapRequest.MaxFrontMatterFields) ||
                    !BridgeDocumentMapLimiter.IsValidLimit(
                        mapRequest.MaxLinks) ||
                    !BridgeDocumentMapLimiter.IsValidByteLimit(
                        mapRequest.MaxResponseBytes))
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Document map request is invalid.");
                }
                var documentMap = await vault.ExecuteAsync(
                    (service, token) => service.GetDocumentMapAsync(
                        mapRequest.DocumentId,
                        ReadScope(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
                return BridgeDocumentMapLimiter.Apply(
                    documentMap,
                    mapRequest.MaxHeadings,
                    mapRequest.MaxFrontMatterFields,
                    mapRequest.MaxLinks,
                    mapRequest.MaxResponseBytes);
            case "admin.notes.backlinks":
                var backlinksRequest =
                    PipeRpcCodec.ReadPayload<AdminBacklinksRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                if (backlinksRequest.DocumentId == Guid.Empty ||
                    backlinksRequest.Limit is < 1 or > 100)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Backlink request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.ListBacklinksAsync(
                        backlinksRequest.DocumentId,
                        backlinksRequest.Cursor,
                        backlinksRequest.Limit,
                        ReadScope(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.notes.unlinked-mentions":
                var unlinkedRequest =
                    PipeRpcCodec.ReadPayload<AdminUnlinkedMentionsRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                if (unlinkedRequest.DocumentId == Guid.Empty ||
                    unlinkedRequest.Limit is < 1 or > 100)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Unlinked mention request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.ListUnlinkedMentionsAsync(
                        unlinkedRequest.DocumentId,
                        unlinkedRequest.Cursor,
                        unlinkedRequest.Limit,
                        ReadScope(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.tags.list":
                var tagsRequest = PipeRpcCodec.ReadPayload<AdminTagListRequest>(
                    new PipeRpcRequest(
                        PipeProtocol.Version,
                        Guid.Empty,
                        method,
                        payload));
                if (tagsRequest.Limit is < 1 or > 100)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Tag request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.ListTagsAsync(
                        tagsRequest.Cursor,
                        tagsRequest.Limit,
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.graph.local":
                var graphRequest =
                    PipeRpcCodec.ReadPayload<AdminLocalGraphRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                if (graphRequest.DocumentId == Guid.Empty ||
                    graphRequest.Depth is < 1 or > 2 ||
                    graphRequest.MaxNodes is < 1 or > 200 ||
                    graphRequest.MaxEdges is < 1 or > 500)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Local graph request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.GetLocalGraphAsync(
                        new VaultLocalGraphQuery(
                            graphRequest.DocumentId,
                            graphRequest.Depth,
                            graphRequest.Tags ?? [],
                            graphRequest.Folder,
                            graphRequest.MaxNodes,
                            graphRequest.MaxEdges),
                        ReadScope(session),
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
                            updateRequest.Request.Tags,
                            updateRequest.Request.UpdateBacklinks),
                        Actor(session),
                        token),
                    cancellationToken).ConfigureAwait(false);
            case "admin.notes.patch":
                var patchRequest =
                    PipeRpcCodec.ReadPayload<AdminPatchNoteRequest>(
                        new PipeRpcRequest(
                            PipeProtocol.Version,
                            Guid.Empty,
                            method,
                            payload));
                if (patchRequest.DocumentId == Guid.Empty ||
                    patchRequest.Request.ExpectedRevision < 1)
                {
                    throw new AdminPipeRpcException(
                        "invalid_input",
                        "Patch request is invalid.");
                }
                return await vault.ExecuteAsync(
                    (service, token) => service.PatchNoteAsync(
                        patchRequest.DocumentId,
                        patchRequest.Request.ExpectedRevision,
                        new VaultNotePatchCommand(
                            patchRequest.Request.TargetKind,
                            patchRequest.Request.HeadingPath,
                            patchRequest.Request.FrontMatterKey,
                            patchRequest.Request.Operation,
                            patchRequest.Request.ContentMarkdown,
                            patchRequest.Request.Value,
                            patchRequest.Request.DryRun),
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
            case "admin.maintenance.rebuild_links":
                return await vault.ExecuteMaintenanceAsync(
                    (service, token) => service.RebuildLinkIndexAsync(
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
                        new VaultAuditQuery(
                            auditRequest.DocumentId,
                            auditRequest.Action,
                            auditRequest.CorrelationId,
                            auditRequest.Cursor,
                            auditRequest.Limit),
                        token),
                    cancellationToken).ConfigureAwait(false);
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
                    VerifyTransferManifest(transfers, backupWorkspace);
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
                    VerifyTransferManifest(transfers, exportWorkspace);
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
                        FreezeAndVerifyImport(transfers, workspace);
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
                    if (!_importWorkspaces.Contains(commitRequest.JobId))
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
                    if (!_importWorkspaces.TryTake(
                            commitRequest.JobId,
                            out var importWorkspace) ||
                        importWorkspace is null)
                    {
                        throw new AdminPipeRpcException(
                            "not_found",
                            "Import job was not found.");
                    }
                    using (importWorkspace)
                    {
                        VerifyTransferManifest(transfers, importWorkspace);
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
                    FreezeAndVerifyImport(transfers, restoreWorkspace);
                    using var restoreExportWorkspace = transfers.CreateWorkspace(
                        HostTransferDirection.Export,
                        HostTransferPayloadKind.Directory,
                        session.Client.UserSid,
                        session.Client.LogonSid,
                        "restored");
                    var restored = await sessions.RestoreBackupToNewAsync(
                        restoreWorkspace.PayloadPath,
                        restoreExportWorkspace.PayloadPath,
                        Actor(session),
                        cancellationToken).ConfigureAwait(false);
                    transfers.FreezeManifest(restoreExportWorkspace);
                    VerifyTransferManifest(transfers, restoreExportWorkspace);
                    var exportTransfer = await transferPrompt.TransferAsync(
                        new HostTransferRequest(
                            restoreExportWorkspace.TransferId,
                            HostTransferDirection.Export,
                            HostTransferPayloadKind.Directory,
                            restoreExportWorkspace.PayloadPath,
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
            case "admin.security.diagnostics":
                {
                    var diagnosticSnapshot = await sessions
                        .GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var install = ReadInstallConsistency(cancellationToken);
                    return new AdminSecurityDiagnosticsData(
                        typeof(StrictAdminVaultRpcProvider).Assembly
                            .GetName().Version?.ToString() ?? "1.0.0",
                        PipeProtocol.Version,
                        configuration?.SecurityMode
                            ?? CipherVaultServiceConfiguration.StrictModeValue,
                        configuration?.OwnerSid ?? session.OwnerSid,
                        install.Manifest?.ServiceSid,
                        session.Client.SessionId,
                        brokerConnections?.IsAvailable ?? false,
                        diagnosticSnapshot.State.ToString(),
                        diagnosticSnapshot.SessionId,
                        install.Consistency);
                }
            case "admin.agent.bridges.list":
                return (configuration?.Agent?.Bridges ?? [])
                    .Select(bridge => new AdminStrictBridgeData(
                        bridge.Id ?? string.Empty,
                        string.Equals(
                            bridge.AccessMode,
                            "readWrite",
                            StringComparison.OrdinalIgnoreCase)
                            ? "readWrite"
                            : "readOnly",
                        bridge.Enabled != false,
                        bridge.Image?.Path,
                        bridge.Image?.Sha256,
                        bridge.Image?.SignerThumbprint,
                        bridge.Image?.SignerSubject))
                    .ToArray();
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

    private (CipherVaultInstallPublicManifest? Manifest,
        AdminInstallConsistencyData Consistency) ReadInstallConsistency(
        CancellationToken cancellationToken)
    {
        if (configuration is null || paths is null)
        {
            return (
                null,
                new AdminInstallConsistencyData(
                    ManifestLoaded: false,
                    ErrorCode: "not_configured",
                    InstallId: null,
                    OwnerSidMatches: false,
                    ServiceAccountMatches: false,
                    PipesMatch: false));
        }
        try
        {
            var manifest = CipherVaultServiceConfiguration.LoadInstallManifest(
                paths.PublicManifestPath,
                cancellationToken);
            return (
                manifest,
                new AdminInstallConsistencyData(
                    ManifestLoaded: true,
                    ErrorCode: null,
                    InstallId: manifest.InstallId,
                    OwnerSidMatches: string.Equals(
                        manifest.OwnerSid,
                        configuration.OwnerSid,
                        StringComparison.OrdinalIgnoreCase),
                    ServiceAccountMatches: string.Equals(
                        manifest.ServiceAccount,
                        configuration.ServiceAccount,
                        StringComparison.Ordinal),
                    PipesMatch: PipesMatch(manifest.Pipes, configuration.Pipes)));
        }
        catch (VaultException exception)
        {
            return (
                null,
                new AdminInstallConsistencyData(
                    ManifestLoaded: false,
                    ErrorCode: exception.Code switch
                    {
                        VaultErrorCode.NotFound => "not_found",
                        VaultErrorCode.InvalidInput => "invalid_manifest",
                        _ => "manifest_unavailable"
                    },
                    InstallId: null,
                    OwnerSidMatches: false,
                    ServiceAccountMatches: false,
                    PipesMatch: false));
        }
    }

    private static bool PipesMatch(
        CipherVaultPipeConfigFile? left,
        CipherVaultPipeConfigFile? right)
    {
        return left is not null &&
            right is not null &&
            string.Equals(left.Admin, right.Admin, StringComparison.Ordinal) &&
            string.Equals(left.Broker, right.Broker, StringComparison.Ordinal) &&
            string.Equals(left.Bridge, right.Bridge, StringComparison.Ordinal) &&
            left.RejectRemoteClients == right.RejectRemoteClients;
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

    private static VaultReadScope ReadScope(AdminSession session)
    {
        return session.PathPolicy is { GlobalRead: false } pathPolicy
            ? VaultReadScope.Restricted(pathPolicy.ReadPaths)
            : VaultReadScope.Unrestricted;
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

    private static void VerifyTransferManifest(
        TransferCoordinator transfers,
        TransferWorkspace workspace)
    {
        try
        {
            transfers.VerifyManifest(workspace);
        }
        catch (VaultException exception) when (
            exception.Code == VaultErrorCode.InvalidInput)
        {
            throw new AdminPipeRpcException(
                "transfer_invalid",
                exception.Message);
        }
    }

    private static void FreezeAndVerifyImport(
        TransferCoordinator transfers,
        TransferWorkspace workspace)
    {
        try
        {
            transfers.EnsureNoReparsePoints(workspace.PayloadPath);
            transfers.FreezeManifest(workspace);
            transfers.VerifyManifest(workspace);
        }
        catch (VaultException exception) when (
            exception.Code == VaultErrorCode.InvalidInput)
        {
            throw new AdminPipeRpcException(
                "transfer_invalid",
                exception.Message);
        }
    }

    public void Dispose()
    {
        _importWorkspaces.Dispose();
    }
}
