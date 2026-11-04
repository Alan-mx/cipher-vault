using CipherVault.Core.Documents;
using CipherVault.Core.Storage.Documents;
using CipherVault.Application.Backup;
using CipherVault.Application.Drafts;
using CipherVault.Application.Vault;
using CipherVault.Core.Drafts;

namespace CipherVault.Application.Documents;

public sealed class DocumentService
{
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;
    private readonly IDocumentMutationObserver? mutationObserver;
    private readonly IdempotencyService idempotency;

    public DocumentService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null,
        IDocumentMutationObserver? mutationObserver = null,
        IdempotencyService? idempotency = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.mutationObserver = mutationObserver;
        this.idempotency = idempotency ?? new IdempotencyService();
    }

    public async Task<DocumentWriteResult> CreateAsync(
        CreateDocumentRequest request,
        VaultActorContext actor,
        CancellationToken cancellationToken = default) =>
        await CreateCoreAsync(
            request,
            actor,
            idempotencyKey: null,
            cancellationToken);

    public async Task<DocumentWriteResult> CreateIdempotentAsync(
        CreateDocumentRequest request,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await CreateCoreAsync(
            request,
            actor,
            idempotencyKey,
            cancellationToken);

    private async Task<DocumentWriteResult> CreateCoreAsync(
        CreateDocumentRequest request,
        VaultActorContext actor,
        Guid? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var result = await sessionManager.ExecuteDocumentWriteAsync(
            actor,
            async (context, token) =>
            {
                var now = timeProvider.GetUtcNow();
                return await IdempotentDocumentWrite.ExecuteAsync(
                    context,
                    actor,
                    idempotencyKey,
                    IdempotencyOperation.DocumentCreate,
                    new
                    {
                        request.Title,
                        request.VirtualPath,
                        request.Folder,
                        format = request.Format,
                        request.Content,
                        request.Tags
                    },
                    idempotency,
                    now,
                    () => DocumentMutationWriter.CreateAsync(
                        context,
                        request,
                        actor,
                        now,
                        token),
                    token);
            },
            cancellationToken);
        if (result.AuditEventId is not null)
        {
            await mutationObserver.NotifySafelyAsync(DocumentMutationKind.Create);
        }
        return result;
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

    public async Task<DocumentWriteResult> UpdateAsync(
        Guid documentId,
        int expectedRevision,
        UpdateDocumentRequest request,
        VaultActorContext actor,
        CancellationToken cancellationToken = default) =>
        await UpdateCoreAsync(
            documentId,
            expectedRevision,
            request,
            actor,
            idempotencyKey: null,
            cancellationToken);

    public async Task<DocumentWriteResult> UpdateIdempotentAsync(
        Guid documentId,
        int expectedRevision,
        UpdateDocumentRequest request,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await UpdateCoreAsync(
            documentId,
            expectedRevision,
            request,
            actor,
            idempotencyKey,
            cancellationToken);

    private async Task<DocumentWriteResult> UpdateCoreAsync(
        Guid documentId,
        int expectedRevision,
        UpdateDocumentRequest request,
        VaultActorContext actor,
        Guid? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var result = await sessionManager.ExecuteDocumentWriteAsync(
            actor,
            async (context, token) =>
            {
                var now = timeProvider.GetUtcNow();
                return await IdempotentDocumentWrite.ExecuteAsync(
                    context,
                    actor,
                    idempotencyKey,
                    IdempotencyOperation.DocumentUpdate,
                    new
                    {
                        documentId,
                        expectedRevision,
                        request
                    },
                    idempotency,
                    now,
                    () => DocumentMutationWriter.UpdateAsync(
                        context,
                        documentId,
                        expectedRevision,
                        request,
                        actor,
                        now,
                        token),
                    token);
            },
            cancellationToken);
        if (result.AuditEventId is not null)
        {
            await mutationObserver.NotifySafelyAsync(DocumentMutationKind.Update);
        }
        return result;
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

    public async Task<PatchResult> PatchAsync(
        Guid documentId,
        int expectedRevision,
        DocumentPatchCommand command,
        VaultActorContext actor,
        CancellationToken cancellationToken = default) =>
        await PatchCoreAsync(
            documentId,
            expectedRevision,
            command,
            actor,
            idempotencyKey: null,
            cancellationToken);

    public async Task<PatchResult> PatchIdempotentAsync(
        Guid documentId,
        int expectedRevision,
        DocumentPatchCommand command,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await PatchCoreAsync(
            documentId,
            expectedRevision,
            command,
            actor,
            idempotencyKey,
            cancellationToken);

    private async Task<PatchResult> PatchCoreAsync(
        Guid documentId,
        int expectedRevision,
        DocumentPatchCommand command,
        VaultActorContext actor,
        Guid? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        var result = await sessionManager.ExecuteDocumentWriteAsync(
            actor,
            async (context, token) =>
            {
                var now = timeProvider.GetUtcNow();
                if (idempotencyKey is null && command.DryRun)
                {
                    return await DocumentPatchWriter.ApplyAsync(
                        context,
                        documentId,
                        expectedRevision,
                        command,
                        actor,
                        now,
                        token);
                }
                return await IdempotentDocumentWrite.ExecuteAsync(
                    context,
                    actor,
                    idempotencyKey,
                    IdempotencyOperation.DocumentPatch,
                    new
                    {
                        documentId,
                        expectedRevision,
                        command
                    },
                    idempotency,
                    now,
                    () => DocumentPatchWriter.ApplyAsync(
                        context,
                        documentId,
                        expectedRevision,
                        command,
                        actor,
                        now,
                        token),
                    token);
            },
            cancellationToken);
        if (!command.DryRun && result.AuditEventId is not null)
        {
            await mutationObserver.NotifySafelyAsync(DocumentMutationKind.Patch);
        }

        return result;
    }

    public async Task<TrashDeleteResult> DeleteAsync(
        Guid documentId,
        int expectedRevision,
        VaultActorContext actor,
        CancellationToken cancellationToken = default) =>
        await DeleteCoreAsync(
            documentId,
            expectedRevision,
            actor,
            idempotencyKey: null,
            cancellationToken);

    public async Task<TrashDeleteResult> DeleteIdempotentAsync(
        Guid documentId,
        int expectedRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await DeleteCoreAsync(
            documentId,
            expectedRevision,
            actor,
            idempotencyKey,
            cancellationToken);

    private async Task<TrashDeleteResult> DeleteCoreAsync(
        Guid documentId,
        int expectedRevision,
        VaultActorContext actor,
        Guid? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var result = await sessionManager.ExecuteDocumentWriteAsync(
            actor,
            async (context, token) =>
            {
                var now = timeProvider.GetUtcNow();
                return await IdempotentDocumentWrite.ExecuteAsync(
                    context,
                    actor,
                    idempotencyKey,
                    IdempotencyOperation.DocumentDelete,
                    new { documentId, expectedRevision },
                    idempotency,
                    now,
                    () => DocumentMutationWriter.DeleteAsync(
                        context,
                        documentId,
                        expectedRevision,
                        actor,
                        now,
                        token),
                    token);
            },
            cancellationToken);
        if (result.AuditEventId is not null)
        {
            await mutationObserver.NotifySafelyAsync(DocumentMutationKind.Delete);
        }
        return result;
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
