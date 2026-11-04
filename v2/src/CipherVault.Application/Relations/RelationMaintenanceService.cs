using CipherVault.Application.Drafts;
using CipherVault.Application.Vault;
using CipherVault.Core.Audit;
using CipherVault.Core.Documents;
using CipherVault.Core.Storage.Relations;

namespace CipherVault.Application.Relations;

public sealed record RelationMaintenanceResult(
    int DeletedSuggestions,
    int DeletedIdempotencyRecords);

public sealed class RelationMaintenanceService(
    VaultSessionManager sessionManager,
    IdempotencyService idempotency,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<RelationMaintenanceResult> CleanupAsync(
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteMaintenanceAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var now = clock.GetUtcNow();
                    var deletedSuggestions =
                        await RelationSuggestionStore.DeleteTerminalBeforeAsync(
                            context,
                            now - Retention,
                            token);
                    var deletedIdempotency =
                        await idempotency.CleanupExpiredAsync(
                            context,
                            now,
                            token);
                    if (deletedSuggestions > 0)
                    {
                        await AuditStore.AppendAsync(
                            context,
                            new VaultActorContext(VaultActorKind.System),
                            "relation_suggestion.cleanup",
                            documentId: null,
                            revision: null,
                            result: "success",
                            details: new Dictionary<string, object?>
                            {
                                ["deletedSuggestions"] =
                                    deletedSuggestions,
                                ["sessionGeneration"] =
                                    context.SessionGeneration
                            },
                            occurredAt: now,
                            cancellationToken: token);
                    }
                    await context.CommitTransactionAsync(token);
                    return new RelationMaintenanceResult(
                        deletedSuggestions,
                        deletedIdempotency);
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            TimeSpan.FromSeconds(30),
            cancellationToken).AsTask();
}
