using CipherVault.Core.Audit;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Storage.Documents;

public static class DocumentMaintenanceStore
{
    public static async Task<LinkRebuildResult> RebuildLinksAsync(
        VaultOperationContext context,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Link rebuild requires a user action.");
        }

        var documents = new List<LinkRebuildSource>();
        using (var read = context.CreateCommand())
        {
            read.Command.CommandText =
                """
                SELECT document_id, revision, format, content
                FROM documents
                WHERE deleted_at IS NULL
                ORDER BY path_key, document_id;
                """;
            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                documents.Add(new LinkRebuildSource(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        using (var delete = context.CreateCommand())
        {
            delete.Command.CommandText = "DELETE FROM document_links;";
            await delete.Command.ExecuteNonQueryAsync(cancellationToken);
        }

        var linksIndexed = 0;
        foreach (var document in documents)
        {
            var links = document.Format == "md"
                ? WikiLinkParser.Parse(document.Content)
                : [];
            await DocumentIndexWriter.ReplaceLinksAsync(
                context,
                document.DocumentId,
                document.Revision,
                links,
                cancellationToken);
            linksIndexed += links.Count;
        }

        var auditId = await AuditStore.AppendAsync(
            context,
            actor,
            "link_rebuild",
            null,
            null,
            "success",
            new
            {
                documents = documents.Count,
                links = linksIndexed
            },
            now,
            cancellationToken);
        return new LinkRebuildResult(documents.Count, linksIndexed, auditId);
    }

    private sealed record LinkRebuildSource(
        Guid DocumentId,
        int Revision,
        string Format,
        string Content);
}
