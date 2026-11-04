using System.Text.Json;
using CipherVault.Application.Vault;
using CipherVault.Core.Audit;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Drafts;

public sealed class McpWriteModeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);
    private readonly VaultSessionManager sessionManager;
    private readonly IdempotencyService idempotency;
    private readonly TimeProvider timeProvider;

    public McpWriteModeService(
        VaultSessionManager sessionManager,
        IdempotencyService idempotency,
        TimeProvider? timeProvider = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.idempotency = idempotency ??
            throw new ArgumentNullException(nameof(idempotency));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public McpWriteModeStatus Get()
    {
        var snapshot = sessionManager.GetMcpWriteMode();
        return new McpWriteModeStatus(
            snapshot.Mode,
            snapshot.SessionId,
            snapshot.SessionGeneration,
            snapshot.UpdatedAt,
            snapshot.Effective);
    }

    public Task<McpWriteModeStatus> ChangeAsync(
        McpWriteModeChangeRequest request,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (!Enum.IsDefined(request.Mode))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "MCP write mode is invalid.");
        }
        IdempotencyService.RequireKey(idempotencyKey);
        var requestElement = JsonSerializer.SerializeToElement(
            new { mode = request.Mode.ToString().ToLowerInvariant() },
            JsonOptions);

        return sessionManager.ChangeMcpWriteModeAsync(
            actor,
            request.Mode,
            async (context, previousMode, token) =>
            {
                var sessionId = previousMode.SessionId
                    ?? throw new VaultException(
                        VaultErrorCode.VaultLocked,
                        "The Vault Session is unavailable.");
                var requestHash = IdempotencyRequestHasher.Compute(
                    IdempotencyOperation.McpWriteModeChange,
                    actor.Kind,
                    sessionId,
                    requestElement);
                await context.BeginTransactionAsync(token);
                var replay = await idempotency.TryReplayAsync(
                    context,
                    idempotencyKey,
                    IdempotencyOperation.McpWriteModeChange,
                    actor,
                    sessionId,
                    requestHash,
                    token);
                if (replay is not null)
                {
                    if (previousMode.Mode != request.Mode)
                    {
                        throw new VaultException(
                            VaultErrorCode.IdempotencyKeyReused,
                            "The idempotency key belongs to a stale write-mode state.");
                    }
                    return new McpWriteModeChangeOutcome<McpWriteModeStatus>(
                        IdempotencyService.Replay<McpWriteModeStatus>(replay),
                        ApplyMode: false);
                }

                var now = timeProvider.GetUtcNow();
                await AuditStore.AppendAsync(
                    context,
                    actor,
                    "mcp_write_mode.change",
                    documentId: null,
                    revision: null,
                    result: "success",
                    details: new
                    {
                        previousMode = previousMode.Mode
                            .ToString()
                            .ToLowerInvariant(),
                        mode = request.Mode.ToString().ToLowerInvariant(),
                        sessionGeneration = previousMode.SessionGeneration
                    },
                    occurredAt: now,
                    token);
                var status = new McpWriteModeStatus(
                    request.Mode,
                    previousMode.SessionId,
                    previousMode.SessionGeneration,
                    now,
                    Effective: true);
                await idempotency.SaveAsync(
                    context,
                    idempotencyKey,
                    IdempotencyOperation.McpWriteModeChange,
                    actor,
                    sessionId,
                    requestHash,
                    status,
                    now,
                    token);
                await context.CommitTransactionAsync(token);
                return new McpWriteModeChangeOutcome<McpWriteModeStatus>(
                    status,
                    ApplyMode: true);
            },
            cancellationToken);
    }
}
