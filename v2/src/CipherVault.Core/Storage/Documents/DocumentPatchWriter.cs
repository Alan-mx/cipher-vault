using CipherVault.Core.Database;
using CipherVault.Core.Documents;

namespace CipherVault.Core.Storage.Documents;

public static class DocumentPatchWriter
{
    public static async Task<PatchResult> ApplyAsync(
        VaultOperationContext context,
        Guid documentId,
        int expectedRevision,
        DocumentPatchCommand command,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        DocumentMutationWriter.EnsureDocumentWriteActor(actor);
        var current = await DocumentMutationWriter.ReadCurrentForMutationAsync(
            context,
            documentId,
            cancellationToken);
        DocumentMutationWriter.ThrowIfRevisionMismatch(
            expectedRevision,
            current.Revision);

        var coreCommand = new VaultPatchCommand(
            command.TargetKind == VaultPatchTargetKind.Heading
                ? "heading"
                : "frontmatter",
            command.HeadingPath,
            command.FrontMatterKey,
            command.Operation switch
            {
                VaultPatchOperation.Append => "append",
                VaultPatchOperation.Prepend => "prepend",
                VaultPatchOperation.Replace => "replace",
                VaultPatchOperation.Delete => "delete",
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            },
            command.ContentMarkdown,
            command.Value,
            command.DryRun);
        var computation = DocumentPatchEngine.Apply(current.Content, coreCommand);
        if (command.DryRun || !computation.Changed)
        {
            return new PatchResult(
                documentId,
                current.Revision,
                current.Revision,
                command.DryRun,
                computation.Changed,
                computation.UnifiedDiff,
                computation.DiffTruncated,
                null);
        }

        var tags = FrontMatterTags.TryExtract(
            computation.Content,
            out var frontMatterTags)
            ? TagNormalizer.NormalizeMany(frontMatterTags)
                .Select(tag => tag.Name)
                .ToArray()
            : current.TagNames;
        var updated = await DocumentMutationWriter.UpdateCoreAsync(
            context,
            documentId,
            expectedRevision,
            new UpdateDocumentRequest(
                current.Title,
                current.Path,
                computation.Content,
                tags,
                UpdateBacklinks: false),
            actor,
            now,
            "patch",
            DocumentContentLimitMode.StandardWrite,
            cancellationToken);
        return new PatchResult(
            documentId,
            current.Revision,
            updated.Revision,
            DryRun: false,
            Changed: true,
            computation.UnifiedDiff,
            computation.DiffTruncated,
            updated.AuditEventId);
    }
}
