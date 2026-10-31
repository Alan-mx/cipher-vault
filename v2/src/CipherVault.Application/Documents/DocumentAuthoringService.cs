using System.Text;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
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

    public DocumentAuthoringService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<DocumentWriteResult> AppendAsync(
        Guid documentId,
        int expectedRevision,
        string content,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
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

                var result = await DocumentMutationWriter.UpdateAuthoringAsync(
                    context,
                    documentId,
                    expectedRevision,
                    new UpdateDocumentRequest(
                        current.Metadata.Title,
                        current.Metadata.VirtualPath,
                        updatedContent,
                        current.Metadata.Tags,
                        UpdateBacklinks: false),
                    actor,
                    timeProvider.GetUtcNow(),
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken).AsTask();
    }

    public Task<DocumentWriteResult> UpdatePartialAsync(
        Guid documentId,
        int expectedRevision,
        DocumentPartialUpdate update,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(actor);
        ValidatePresence(update);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
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

                var result = await DocumentMutationWriter.UpdateAuthoringAsync(
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
                        tags,
                        update.UpdateBacklinks.IsSpecified
                            ? RequireBoolean(
                                update.UpdateBacklinks.Value,
                                "updateBacklinks")
                            : false),
                    actor,
                    timeProvider.GetUtcNow(),
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken).AsTask();
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
            || update.Tags.IsSpecified
            || update.UpdateBacklinks.IsSpecified;
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

    private static bool RequireBoolean(bool? value, string name) =>
        value ?? throw InvalidInput($"{name} cannot be null.");

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
