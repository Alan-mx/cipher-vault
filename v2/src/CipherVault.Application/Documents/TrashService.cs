using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Storage.Documents;

namespace CipherVault.Application.Documents;

public sealed class TrashService
{
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;

    public TrashService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<TrashPage> ListAsync(
        CursorPageRequest page,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<TrashPage>(
                TrashStore.ListAsync(
                    context,
                    page,
                    timeProvider.GetUtcNow(),
                    token)),
            cancellationToken).AsTask();

    public Task<DocumentWriteResult> RestoreAsync(
        Guid documentId,
        int expectedRevision,
        string? replacementPath,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var result = await TrashStore.RestoreAsync(
                    context,
                    documentId,
                    expectedRevision,
                    replacementPath,
                    actor,
                    timeProvider.GetUtcNow(),
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken).AsTask();
    }

    public Task<Guid?> PurgeAsync(
        Guid documentId,
        int expectedRevision,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var result = await TrashStore.PurgeAsync(
                    context,
                    documentId,
                    expectedRevision,
                    actor,
                    timeProvider.GetUtcNow(),
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken).AsTask();
    }
}
