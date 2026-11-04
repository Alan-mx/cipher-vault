using System.Text;
using CipherVault.Application.Backup;
using CipherVault.Application.Drafts;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Errors;
using CipherVault.Core.Storage.Documents;

namespace CipherVault.Application.Documents;

public sealed class DocumentAuthoringService
{
    private const int MaxAppendBytes = 256 * 1024;
    private const int MaxPartialContentBytes = 256 * 1024;
    private const int MaxDocumentBytes = 4 * 1024 * 1024;
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;
    private readonly IDocumentMutationObserver? mutationObserver;
    private readonly IdempotencyService idempotency;

    public DocumentAuthoringService(
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

    public async Task<DocumentWriteResult> AppendAsync(
        Guid documentId,
        int expectedRevision,
        string content,
        VaultActorContext actor,
        CancellationToken cancellationToken = default) =>
        await AppendCoreAsync(
            documentId,
            expectedRevision,
            content,
            actor,
            idempotencyKey: null,
            cancellationToken);

    public async Task<DocumentWriteResult> AppendIdempotentAsync(
        Guid documentId,
        int expectedRevision,
        string content,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await AppendCoreAsync(
            documentId,
            expectedRevision,
            content,
            actor,
            idempotencyKey,
            cancellationToken);

    private async Task<DocumentWriteResult> AppendCoreAsync(
        Guid documentId,
        int expectedRevision,
        string content,
        VaultActorContext actor,
        Guid? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
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
                    IdempotencyOperation.DocumentAppend,
                    new { documentId, expectedRevision, content },
                    idempotency,
                    now,
                    async () =>
                    {
                        var current = await ReadCurrentAsync(
                            context,
                            documentId,
                            token);
                        var appendBytes = Encoding.UTF8.GetByteCount(content);
                        if (appendBytes > MaxAppendBytes)
                        {
                            throw TooLarge();
                        }

                        var updatedContent = current.Content + content;
                        if (Encoding.UTF8.GetByteCount(updatedContent) > MaxDocumentBytes)
                        {
                            throw TooLarge();
                        }

                        return await DocumentMutationWriter.UpdateAuthoringAsync(
                            context,
                            documentId,
                            expectedRevision,
                            new UpdateDocumentRequest(
                                current.Metadata.Title,
                                current.Metadata.VirtualPath,
                                updatedContent,
                                current.Metadata.Tags),
                            actor,
                            now,
                            token);
                    },
                    token);
            },
            cancellationToken);
        if (result.AuditEventId is not null)
        {
            await mutationObserver.NotifySafelyAsync(DocumentMutationKind.Update);
        }
        return result;
    }

    public async Task<DocumentWriteResult> UpdatePartialAsync(
        Guid documentId,
        int expectedRevision,
        DocumentPartialUpdate update,
        VaultActorContext actor,
        CancellationToken cancellationToken = default) =>
        await UpdatePartialCoreAsync(
            documentId,
            expectedRevision,
            update,
            actor,
            idempotencyKey: null,
            cancellationToken);

    public async Task<DocumentWriteResult> UpdatePartialIdempotentAsync(
        Guid documentId,
        int expectedRevision,
        DocumentPartialUpdate update,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await UpdatePartialCoreAsync(
            documentId,
            expectedRevision,
            update,
            actor,
            idempotencyKey,
            cancellationToken);

    private async Task<DocumentWriteResult> UpdatePartialCoreAsync(
        Guid documentId,
        int expectedRevision,
        DocumentPartialUpdate update,
        VaultActorContext actor,
        Guid? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(actor);
        ValidatePresence(update);
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
                    new { documentId, expectedRevision, update },
                    idempotency,
                    now,
                    async () =>
                    {
                        var current = await ReadCurrentAsync(
                            context,
                            documentId,
                            token);
                        var content = update.Content.IsSpecified
                            ? RequireContent(update.Content.Value)
                            : current.Content;
                        var tags = update.Tags.IsSpecified
                            ? RequireTags(update.Tags.Value)
                            : current.Metadata.Tags;

                        if (update.Content.IsSpecified
                            && Encoding.UTF8.GetByteCount(content) > MaxPartialContentBytes)
                        {
                            throw TooLarge();
                        }

                        if (!update.Content.IsSpecified && update.Tags.IsSpecified)
                        {
                            if (current.Metadata.Format == VaultDocumentFormat.Markdown)
                            {
                                content = DocumentContentAuthoring.RewriteFrontMatterTags(
                                    content,
                                    tags);
                            }
                        }
                        else if (update.Content.IsSpecified && !update.Tags.IsSpecified)
                        {
                            tags = DocumentContentAuthoring.ResolveEffectiveTags(
                                current.Metadata.Format,
                                content,
                                current.Metadata.Tags);
                        }

                        if (Encoding.UTF8.GetByteCount(content) > MaxDocumentBytes)
                        {
                            throw TooLarge();
                        }

                        return await DocumentMutationWriter.UpdateAuthoringAsync(
                            context,
                            documentId,
                            expectedRevision,
                            new UpdateDocumentRequest(
                                update.Title.IsSpecified
                                    ? RequireString(update.Title.Value, "title")
                                    : current.Metadata.Title,
                                update.VirtualPath.IsSpecified
                                    ? RequireString(update.VirtualPath.Value, "virtualPath")
                                    : current.Metadata.VirtualPath,
                                content,
                                tags),
                            actor,
                            now,
                            token).ConfigureAwait(false);
                    },
                    token);
            },
            cancellationToken);
        if (result.AuditEventId is not null)
        {
            await mutationObserver.NotifySafelyAsync(DocumentMutationKind.Update);
        }
        return result;
    }

    private static async Task<CurrentAuthoringDocument> ReadCurrentAsync(
        CipherVault.Core.Database.VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var metadata = await DocumentQueryStore.GetMetadataAsync(
            context,
            documentId,
            cancellationToken);
        var content = new StringBuilder();
        var offset = 0;
        while (true)
        {
            var chunk = await DocumentQueryStore.ReadContentAsync(
                context,
                documentId,
                offset,
                maxChars: 65_536,
                cancellationToken);
            content.Append(chunk.Content);
            if (chunk.Done)
            {
                break;
            }
            offset = chunk.NextOffset;
        }
        return new CurrentAuthoringDocument(metadata, content.ToString());
    }

    private static void ValidatePresence(DocumentPartialUpdate update)
    {
        var any = update.Title.IsSpecified
            || update.VirtualPath.IsSpecified
            || update.Content.IsSpecified
            || update.Tags.IsSpecified;
        if (!any)
        {
            throw InvalidInput("At least one update field is required.");
        }

        ValidateField(update.Title, "title");
        ValidateField(update.VirtualPath, "virtualPath");
        ValidateField(update.Content, "content");
        ValidateField(update.Tags, "tags");
    }

    private static void ValidateField<T>(
        OptionalField<T> field,
        string name)
    {
        if (field.IsSpecified && field.Value is null)
        {
            throw InvalidInput($"{name} cannot be null.");
        }
    }

    private static string RequireString(string? value, string name) =>
        value ?? throw InvalidInput($"{name} cannot be null.");

    private static string RequireContent(string? value) =>
        value ?? throw InvalidInput("content cannot be null.");

    private static IReadOnlyList<string> RequireTags(
        IReadOnlyList<string>? value) =>
        value ?? throw InvalidInput("tags cannot be null.");

    private static VaultException InvalidInput(string message) =>
        new(VaultErrorCode.InvalidInput, message);

    private static VaultException TooLarge() =>
        new(
            VaultErrorCode.DocumentTooLarge,
            "Document content exceeds the allowed size.");

    private sealed record CurrentAuthoringDocument(
        DocumentMetadata Metadata,
        string Content);
}
