using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;

namespace CipherVault.Core.Storage.Documents;

internal static class DocumentRevisionWriter
{
    internal static async Task<Guid> InsertAsync(
        VaultOperationContext context,
        Guid documentId,
        int revision,
        string title,
        string path,
        string format,
        string content,
        string frontMatterJson,
        string headingsJson,
        IReadOnlyList<NormalizedTag> tags,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var revisionId = Guid.NewGuid();
        using var insert = context.CreateCommand();
        insert.Command.CommandText =
            """
            INSERT INTO document_revisions(
                revision_id, document_id, revision, title, path, format,
                content, frontmatter_json, headings_json, tags_json,
                created_at, retain_until)
            VALUES(
                $revisionId, $documentId, $revision, $title, $path, $format,
                $content, $frontMatter, $headings, $tags,
                $createdAt, $retainUntil);
            """;
        insert.Command.Parameters.AddWithValue(
            "$revisionId",
            revisionId.ToString("D"));
        insert.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        insert.Command.Parameters.AddWithValue("$revision", revision);
        insert.Command.Parameters.AddWithValue("$title", title);
        insert.Command.Parameters.AddWithValue("$path", path);
        insert.Command.Parameters.AddWithValue("$format", format);
        insert.Command.Parameters.AddWithValue("$content", content);
        insert.Command.Parameters.AddWithValue("$frontMatter", frontMatterJson);
        insert.Command.Parameters.AddWithValue("$headings", headingsJson);
        insert.Command.Parameters.AddWithValue(
            "$tags",
            JsonSerializer.Serialize(tags.Select(tag => tag.Name).ToArray()));
        insert.Command.Parameters.AddWithValue(
            "$createdAt",
            now.ToUniversalTime().ToString("O"));
        insert.Command.Parameters.AddWithValue(
            "$retainUntil",
            now.AddDays(90).ToUniversalTime().ToString("O"));
        await insert.Command.ExecuteNonQueryAsync(cancellationToken);
        return revisionId;
    }
}
