using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;
using Microsoft.Extensions.Hosting;

namespace CipherVault.Host.Service;

public sealed class StrictBridgeHostedService(
    CipherVaultServiceConfigFile configuration,
    CipherVaultServicePaths paths,
    StrictServiceHostOptions options,
    HostSessionCoordinator sessions,
    AccessGrantService grants,
    IHostAccessGrantPrompt grantPrompt,
    StrictAdminVaultRpcProvider rpc) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration.Agent?.CompatibilityHttpEnabled != false)
        {
            return;
        }
        if (!options.EnableBridgePipe)
        {
            return;
        }
        var bridge = configuration.Agent.Bridges?.FirstOrDefault(item =>
            item.Enabled != false &&
            string.Equals(item.Id, "mcp", StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid("Strict MCP bridge policy is missing.");
        var install = CipherVaultServiceConfiguration.LoadInstallManifest(
            paths.PublicManifestPath);
        var serviceSid = install.ServiceSid
            ?? throw Invalid("Service SID is required.");
        var ownerSid = configuration.OwnerSid
            ?? throw Invalid("Owner SID is required.");
        var servicePolicy = AdminPipeHostedService.CreateServicePolicy(
            configuration,
            serviceSid,
            paths.InstallRoot);
        var verification = WindowsProcessIdentityVerifier.Verify(
            (uint)Environment.ProcessId,
            servicePolicy);
        if (verification.Status != ProcessVerificationStatus.Verified ||
            verification.Evidence is null)
        {
            verification.Evidence?.Dispose();
            throw Invalid("Host service identity verification failed.");
        }
        using var evidence = verification.Evidence;
        var bridgePolicy = AdminPipeHostedService.CreateBridgePolicy(
            configuration,
            ownerSid,
            paths.InstallRoot);
        var security = PipeSecurityFactory.Create(
            [new SecurityIdentifier(serviceSid)],
            [new SecurityIdentifier(ownerSid)]);
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = NamedPipeServerFactory.Create(
                configuration.Pipes?.Bridge
                    ?? throw Invalid("Bridge pipe is required."),
                security);
            try
            {
                await AcceptAsync(
                    pipe,
                    evidence.Identity,
                    serviceSid,
                    ownerSid,
                    bridge,
                    bridgePolicy,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task AcceptAsync(
        NamedPipeServerStream pipe,
        VerifiedProcessIdentity serviceIdentity,
        string serviceSid,
        string ownerSid,
        CipherVaultStrictBridgeConfigFile bridge,
        ProcessVerificationPolicy clientPolicy,
        CancellationToken cancellationToken)
    {
        await pipe.WaitForConnectionAsync(cancellationToken);
        await using var connection = new PipeJsonConnection(pipe);
        var clientPid = NamedPipeProcessIdentity.GetClientProcessId(
            pipe.SafePipeHandle);
        var result = WindowsProcessIdentityVerifier.Verify(
            clientPid,
            clientPolicy);
        if (result.Status != ProcessVerificationStatus.Verified ||
            result.Evidence is null)
        {
            result.Evidence?.Dispose();
            throw new ProcessIdentityRejectedException(
                result.Status,
                result.ReasonCode ?? "bridge_identity_rejected");
        }
        using var clientEvidence = result.Evidence;
        var client = clientEvidence.Identity;
        if (client.SessionId == 0 ||
            !string.Equals(client.UserSid, ownerSid, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "bridge_owner_mismatch");
        }
        var hello = await connection.ReadAsync<PipeHelloRequest>(cancellationToken);
        if (!string.Equals(
                hello.ProtocolVersion,
                PipeProtocol.Version,
                StringComparison.Ordinal) ||
            !PipeNonce.IsValid(hello.ClientNonceB64Url))
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "bridge_hello_invalid");
        }
        var serverNonce = PipeNonce.Create();
        await connection.WriteAsync(new PipeHelloResponse(
            PipeProtocol.Version,
            serverNonce,
            Guid.NewGuid().ToString("D"),
            serviceSid,
            serviceIdentity.ProcessId,
            serviceIdentity.ProcessStartedAt,
            serviceIdentity.ImageSha256,
            serviceIdentity.SignerThumbprint ?? string.Empty), cancellationToken);
        var authRequest = await connection.ReadAsync<PipeAuthenticateRequest>(
            cancellationToken);
        var transcript = PipeHandshakeTranscript.Compute(
            NamedPipeServerFactory.NormalizePipeName(
                configuration.Pipes!.Bridge!),
            PipeProtocol.Version,
            hello.ClientNonceB64Url,
            serverNonce,
            client,
            serviceIdentity);
        if (authRequest.ClientKind != VerifiedProcessKind.McpBridge ||
            !PipeHandshakeTranscript.FixedTimeEquals(
                transcript,
                authRequest.TranscriptHashB64Url))
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "bridge_handshake_mismatch");
        }
        var processSessionId = Guid.NewGuid();
        await connection.WriteAsync(new PipeAuthenticateResponse(
            processSessionId,
            AdminSessionId: null,
            ownerSid,
            client.SessionId,
            DateTimeOffset.UtcNow.AddHours(12)), cancellationToken);

        AccessGrantContext? grant = null;
        Guid? grantVaultSessionId = null;
        var bridgeInstanceId = Guid.NewGuid();
        while (true)
        {
            PipeRpcRequest request;
            try
            {
                request = await connection.ReadAsync<PipeRpcRequest>(
                    cancellationToken);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            PipeRpcResponse response;
            try
            {
                if (request.Method == "bridge.grant.request")
                {
                    var grantRequest = PipeRpcCodec.ReadPayload<BridgeGrantRequest>(
                        request);
                    var snapshot = await sessions.GetSnapshotAsync(cancellationToken);
                    if (snapshot.SessionId is null ||
                        snapshot.HardExpiresAt is null ||
                        snapshot.State is not (
                            VaultSessionState.Unlocked or
                            VaultSessionState.Expiring))
                    {
                        throw new AdminPipeRpcException(
                            "auth_required",
                            "Vault must be unlocked.");
                    }
                    var scopes = ParseScopes(grantRequest.Scopes);
                    var accessMode = string.Equals(
                        bridge.AccessMode,
                        "readWrite",
                        StringComparison.OrdinalIgnoreCase)
                        ? McpAccessMode.ReadWrite
                        : McpAccessMode.ReadOnly;
                    var outcome = await grants.RequestAsync(
                        bridge.Id!,
                        bridgeInstanceId,
                        accessMode,
                        scopes,
                        new AccessGrantVaultGeneration(
                            snapshot.SessionId.Value,
                            snapshot.HardExpiresAt.Value),
                        async (approval, token) =>
                        {
                            var prompt = await grantPrompt.RequestAsync(
                                new HostAccessGrantPromptRequest(
                                    approval.ClientId,
                                    approval.ClientInstanceId,
                                    approval.TokenAccessMode,
                                    approval.RequestedScopes,
                                    approval.AllowedScopes,
                                    grants.IdleLifetime,
                                    grants.HardLifetime),
                                token);
                            return new AccessGrantApprovalResult(
                                prompt.Approved,
                                prompt.ApprovedScopes,
                                prompt.Approved ? null : "user-denied");
                        },
                        AgentClientIdentityKind.VerifiedBridgeProcess,
                        processSessionId,
                        cancellationToken);
                    if (outcome.IssuedGrant is null)
                    {
                        throw new AdminPipeRpcException(
                            outcome.ReasonCode ?? "access_denied",
                            "Bridge Access Grant was not approved.");
                    }
                    var record = outcome.IssuedGrant.Record;
                    grant = new AccessGrantContext(
                        record.GrantId,
                        record.ClientId,
                        record.ClientInstanceId,
                        record.Scopes,
                        record.VaultSessionId!.Value,
                        record.IdentityKind,
                        record.VerifiedProcessSessionId);
                    grantVaultSessionId = snapshot.SessionId;
                    response = PipeRpcCodec.Success(
                        request.RequestId,
                        new BridgeGrantData(
                            record.GrantId,
                            record.ClientId,
                            accessMode == McpAccessMode.ReadWrite
                                ? "readWrite"
                                : "readOnly",
                            AccessGrantScopes.ToNames(record.Scopes)
                                .OrderBy(value => value, StringComparer.Ordinal)
                                .ToArray(),
                            record.HardExpiresAt!.Value));
                }
                else if (request.Method == "bridge.tools.call")
                {
                    if (grant is null)
                    {
                        throw new AdminPipeRpcException(
                            "access_grant_required",
                            "Request a Bridge Access Grant first.");
                    }
                    var snapshot = await sessions.GetSnapshotAsync(cancellationToken);
                    if (snapshot.SessionId != grantVaultSessionId ||
                        snapshot.State is not (
                            VaultSessionState.Unlocked or
                            VaultSessionState.Expiring))
                    {
                        throw new AdminPipeRpcException(
                            "access_grant_revoked",
                            "Vault session changed.");
                    }
                    var call = PipeRpcCodec.ReadPayload<BridgeToolCallRequest>(
                        request);
                    var scope = RequiredScope(call.ToolName);
                    if (!AccessGrantScopes.Includes(grant.Scopes, scope) ||
                        !grants.RecordUse(grant))
                    {
                        throw new AdminPipeRpcException(
                            "access_denied",
                            "Access Grant does not allow this operation.");
                    }
                    var adminSession = new AdminSession(
                        Guid.NewGuid(),
                        processSessionId,
                        client,
                        ownerSid,
                        serviceSid,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddMinutes(15),
                        DateTimeOffset.UtcNow.AddHours(4),
                        snapshot.SessionId,
                        AdminSessionState.Active,
                        RevocationReason: null);
                    var adminMethod = MapTool(call);
                    var toolResult = await rpc.HandleAsync(
                        adminSession,
                        adminMethod.Method,
                        adminMethod.Payload,
                        cancellationToken);
                    response = PipeRpcCodec.Success(
                        request.RequestId,
                        JsonSerializer.Serialize(
                            new { ok = true, result = toolResult },
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                }
                else
                {
                    throw new AdminPipeRpcException(
                        "unsupported_method",
                        $"Unsupported bridge method: {request.Method}");
                }
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                response = PipeRpcCodec.Failure(
                    request.RequestId,
                    exception is AdminPipeRpcException rpcException
                        ? rpcException.Code
                        : "bridge_error",
                    exception.Message);
            }
            await connection.WriteAsync(response, cancellationToken);
        }
    }

    private static AccessGrantScope ParseScopes(IReadOnlyList<string> values)
    {
        var scopes = AccessGrantScope.None;
        foreach (var value in values)
        {
            scopes |= value.ToLowerInvariant() switch
            {
                "read" => AccessGrantScope.Read,
                "write" => AccessGrantScope.Write,
                "delete" => AccessGrantScope.Delete,
                _ => throw Invalid("Bridge scope is invalid.")
            };
        }
        return AccessGrantScopes.Normalize(scopes);
    }

    private static AccessGrantScope RequiredScope(string toolName)
    {
        return toolName switch
        {
            "vault_status" or "list_catalog" or "search_notes" or
                "read_note" or "scan_notes" => AccessGrantScope.Read,
            "create_note" or "append_note" or "update_note" =>
                AccessGrantScope.Write,
            "delete_note" => AccessGrantScope.Delete,
            _ => throw new AdminPipeRpcException(
                "unsupported_tool",
                $"Unsupported bridge tool: {toolName}")
        };
    }

    private static (string Method, JsonElement Payload) MapTool(
        BridgeToolCallRequest call)
    {
        var args = call.Arguments;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return call.ToolName switch
        {
            "list_catalog" => ("admin.catalog.list",
                JsonSerializer.SerializeToElement(new AdminCatalogListRequest(
                    GetString(args, "folder"), GetString(args, "cursor"),
                    GetInt(args, "limit", 50)), options)),
            "search_notes" => ("admin.search.notes",
                JsonSerializer.SerializeToElement(new AdminSearchNotesRequest(
                    GetString(args, "query") ?? string.Empty,
                    GetString(args, "folder"), GetString(args, "cursor"),
                    GetInt(args, "limit", 10)), options)),
            "read_note" => ("admin.notes.read",
                JsonSerializer.SerializeToElement(new AdminReadNoteRequest(
                    Guid.Parse(GetString(args, "documentId")!),
                    GetInt(args, "offset", 0),
                    GetInt(args, "maxChars", 8000)), options)),
            "scan_notes" => ("admin.notes.scan",
                JsonSerializer.SerializeToElement(new AdminScanNotesRequest(
                    GetString(args, "cursor"), GetInt(args, "limit", 50)), options)),
            "create_note" => ("admin.notes.create",
                JsonSerializer.SerializeToElement(new HostCreateNoteRequest(
                    GetString(args, "title"), GetString(args, "folder"),
                    GetString(args, "format") ?? "md", GetString(args, "content"),
                    GetStrings(args, "tags")), options)),
            "append_note" => ("admin.notes.append",
                JsonSerializer.SerializeToElement(new AdminAppendNoteRequest(
                    Guid.Parse(GetString(args, "documentId")!),
                    new HostAppendNoteRequest(
                        GetInt(args, "expectedRevision", 0),
                        GetString(args, "content"))), options)),
            "update_note" => ("admin.notes.update",
                JsonSerializer.SerializeToElement(new AdminUpdateNoteRequest(
                    Guid.Parse(GetString(args, "documentId")!),
                    new HostUpdateNoteRequest(
                        GetInt(args, "expectedRevision", 0),
                        GetString(args, "title"), GetString(args, "folder"),
                        GetString(args, "content"), GetStrings(args, "tags"))), options)),
            "delete_note" => ("admin.notes.delete",
                JsonSerializer.SerializeToElement(new AdminTrashNoteRequest(
                    Guid.Parse(GetString(args, "documentId")!),
                    new HostTrashNoteRequest(
                        GetInt(args, "expectedRevision", 0))), options)),
            _ => throw new AdminPipeRpcException(
                "unsupported_tool",
                $"Unsupported bridge tool: {call.ToolName}")
        };
    }

    private static string? GetString(JsonElement args, string name)
    {
        return args.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int GetInt(JsonElement args, string name, int fallback)
    {
        return args.TryGetProperty(name, out var value) &&
            value.TryGetInt32(out var result)
            ? result
            : fallback;
    }

    private static IReadOnlyList<string>? GetStrings(
        JsonElement args,
        string name)
    {
        return args.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : null;
    }

    private static VaultException Invalid(string message)
    {
        return new VaultException(VaultErrorCode.InvalidInput, message);
    }
}
