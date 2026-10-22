using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Ipc;

namespace CipherVault.Host.Service;

public sealed class StrictAdminVaultRpcProvider(HostVaultGateway vault)
{
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
            default:
                throw new AdminPipeRpcException(
                    "unsupported_method",
                    $"Unsupported Admin method: {method}");
        }
    }

    private static VaultActorKind Actor(AdminSession session)
    {
        return session.Client.Kind == VerifiedProcessKind.AdminUi
            ? VaultActorKind.UserUi
            : VaultActorKind.UserCli;
    }
}
