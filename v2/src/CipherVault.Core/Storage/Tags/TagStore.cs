using CipherVault.Core.Database;
using CipherVault.Core.Documents;

namespace CipherVault.Core.Storage.Tags;

internal static class TagStore
{
    internal static async Task ReplaceAsync(
        VaultOperationContext context,
        Guid documentId,
        IReadOnlyList<NormalizedTag> tags,
        CancellationToken cancellationToken)
    {
        using (var delete = context.CreateCommand())
        {
            delete.Command.CommandText =
                "DELETE FROM document_tags WHERE document_id = $documentId;";
            delete.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await delete.Command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tag in tags)
        {
            using var insert = context.CreateCommand();
            insert.Command.CommandText =
                """
                INSERT INTO tags(tag_id, name, normalized_key)
                VALUES($tagId, $name, $normalizedKey)
                ON CONFLICT(normalized_key) DO NOTHING;

                INSERT INTO document_tags(document_id, tag_id)
                SELECT $documentId, tag_id
                FROM tags
                WHERE normalized_key = $normalizedKey
                ON CONFLICT(document_id, tag_id) DO NOTHING;
                """;
            insert.Command.Parameters.AddWithValue(
                "$tagId",
                Guid.NewGuid().ToString("D"));
            insert.Command.Parameters.AddWithValue("$name", tag.Name);
            insert.Command.Parameters.AddWithValue(
                "$normalizedKey",
                tag.NormalizedKey);
            insert.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await insert.Command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static async Task CleanupOrphansAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var lease = context.CreateCommand();
        lease.Command.CommandText =
            """
            DELETE FROM tags
            WHERE NOT EXISTS (
                SELECT 1
                FROM document_tags
                WHERE document_tags.tag_id = tags.tag_id
            );
            """;
        await lease.Command.ExecuteNonQueryAsync(cancellationToken);
    }
}
