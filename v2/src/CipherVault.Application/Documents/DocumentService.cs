using CipherVault.Core.Documents;
using CipherVault.Core.Storage.Documents;
using CipherVault.Application.Vault;

namespace CipherVault.Application.Documents;

public sealed class DocumentService
{
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;

    public DocumentService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<DocumentWriteResult> CreateAsync(
        CreateDocumentRequest request,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var result = await DocumentMutationWriter.CreateAsync(
                    context,
                    request,
                    actor,
                    timeProvider.GetUtcNow(),
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken).AsTask();
    }

    public Task<DocumentMetadata> GetAsync(
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<DocumentMetadata>(
                DocumentQueryStore.GetMetadataAsync(context, documentId, token)),
            cancellationToken).AsTask();

    public Task<DocumentContentChunk> ReadContentAsync(
        Guid documentId,
        int offset,
        int maxChars = 32_768,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<DocumentContentChunk>(
                DocumentQueryStore.ReadContentAsync(
                    context,
                    documentId,
                    offset,
                    maxChars,
                    token)),
            cancellationToken).AsTask();

    public Task<CatalogPage> ListAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<CatalogPage>(
                DocumentQueryStore.ListAsync(
                    context,
                    query,
                    timeProvider.GetUtcNow(),
                    token)),
            cancellationToken).AsTask();

    public Task<DocumentMap> GetMapAsync(
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<DocumentMap>(
                DocumentQueryStore.GetMapAsync(context, documentId, token)),
            cancellationToken).AsTask();

    public Task<DocumentWriteResult> UpdateAsync(
        Guid documentId,
        int expectedRevision,
        UpdateDocumentRequest request,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var result = await DocumentMutationWriter.UpdateAsync(
                    context,
                    documentId,
                    expectedRevision,
                    request,
                    actor,
                    timeProvider.GetUtcNow(),
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken).AsTask();
    }

    public Task<TagPage> ListTagsAsync(
        CursorPageRequest page,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<TagPage>(
                DocumentQueryStore.ListTagsAsync(
                    context,
                    page,
                    timeProvider.GetUtcNow(),
                    token)),
            cancellationToken).AsTask();

    public Task<BacklinkPage> ListBacklinksAsync(
        Guid documentId,
        CursorPageRequest page,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<BacklinkPage>(
                DocumentQueryStore.ListBacklinksAsync(
                    context,
                    documentId,
                    page,
                    timeProvider.GetUtcNow(),
                    token)),
            cancellationToken).AsTask();

    public Task<UnlinkedMentionPage> ListUnlinkedMentionsAsync(
        Guid targetDocumentId,
        CursorPageRequest page,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<UnlinkedMentionPage>(
                DocumentQueryStore.ListUnlinkedMentionsAsync(
                    context,
                    targetDocumentId,
                    page,
                    timeProvider.GetUtcNow(),
                    token)),
            cancellationToken).AsTask();

    public Task<PatchResult> PatchAsync(
        Guid documentId,
        int expectedRevision,
        DocumentPatchCommand command,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                if (!command.DryRun)
                {
                    await context.BeginTransactionAsync(token);
                }

                var result = await DocumentPatchWriter.ApplyAsync(
                    context,
                    documentId,
                    expectedRevision,
                    command,
                    actor,
                    timeProvider.GetUtcNow(),
                    token);
                if (!command.DryRun)
                {
                    await context.CommitTransactionAsync(token);
                }
                return result;
            },
            cancellationToken).AsTask();
    }

    public Task<TrashDeleteResult> DeleteAsync(
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
                var result = await DocumentMutationWriter.DeleteAsync(
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

    public Task<LinkRebuildResult> RebuildLinkIndexAsync(
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var result = await DocumentMaintenanceStore.RebuildLinksAsync(
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
