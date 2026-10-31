using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Storage.Documents;

namespace CipherVault.Application.Documents;

public sealed class DocumentRevisionService
{
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;

    public DocumentRevisionService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<RevisionPage> ListAsync(
        Guid documentId,
        CursorPageRequest page,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<RevisionPage>(
                DocumentRevisionStore.ListAsync(
                    context,
                    documentId,
                    page,
                    timeProvider.GetUtcNow(),
                    token)),
            cancellationToken).AsTask();

    public Task<DocumentWriteResult> RestoreAsync(
        Guid documentId,
        Guid revisionId,
        int expectedRevision,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var result = await DocumentRevisionStore.RestoreAsync(
                    context,
                    documentId,
                    revisionId,
                    expectedRevision,
                    actor,
                    timeProvider.GetUtcNow(),
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken).AsTask();
    }

    public Task<RevisionContent> GetContentAsync(
        Guid documentId,
        Guid revisionId,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<RevisionContent>(
                DocumentRevisionStore.GetContentAsync(
                    context,
                    documentId,
                    revisionId,
                    token)),
            cancellationToken).AsTask();

    public Task<RevisionCleanupResult> CleanupExpiredAsync(
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var result = await DocumentRevisionStore.CleanupExpiredAsync(
                    context,
                    actor,
                    timeProvider.GetUtcNow(),
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken).AsTask();
    }
}
