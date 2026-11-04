using CipherVault.Application.Backup;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Storage.Documents;

namespace CipherVault.Application.Documents;

public sealed class TrashService
{
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;
    private readonly IDocumentMutationObserver? mutationObserver;

    public TrashService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null,
        IDocumentMutationObserver? mutationObserver = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.mutationObserver = mutationObserver;
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

    public async Task<DocumentWriteResult> RestoreAsync(
        Guid documentId,
        int expectedRevision,
        string? replacementPath,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var result = await sessionManager.ExecuteDocumentWriteAsync(
            actor,
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
            cancellationToken);
        if (result.AuditEventId is not null)
        {
            await mutationObserver.NotifySafelyAsync(DocumentMutationKind.Restore);
        }
        return result;
    }

    public async Task<Guid?> PurgeAsync(
        Guid documentId,
        int expectedRevision,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var result = await sessionManager.ExecuteDocumentWriteAsync(
            actor,
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
            cancellationToken);
        if (result is not null)
        {
            await mutationObserver.NotifySafelyAsync(DocumentMutationKind.Purge);
        }
        return result;
    }
}
