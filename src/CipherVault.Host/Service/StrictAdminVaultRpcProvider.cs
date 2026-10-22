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
