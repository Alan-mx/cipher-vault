using System.Text;
using System.Text.RegularExpressions;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage;

internal static class DocumentGraphStore
{
    public static async Task<VaultLocalGraphResult> GetLocalGraphAsync(
        SqliteConnection connection,
        VaultLocalGraphQuery query,
        CancellationToken cancellationToken)
    {
        var centerId = query.CenterDocumentId.ToString("D").ToLowerInvariant();
        await EnsureActiveDocumentAsync(
            connection,
            centerId,
            cancellationToken).ConfigureAwait(false);

        var maxNodes = Math.Clamp(query.MaxNodes, 1, 200);
        var maxEdges = Math.Clamp(query.MaxEdges, 1, 500);
        var depth = Math.Clamp(query.Depth, 1, 2);
        var tags = query.Tags
            .Select(VirtualPathPolicy.NormalizeTag)
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var normalizedFolder = VirtualPathPolicy.NormalizeFolder(query.Folder)
            .ToLowerInvariant();

        var sql = new StringBuilder(
            """
            WITH RECURSIVE walk(document_id, depth) AS (
                SELECT $center, 0
                UNION
                SELECT CASE
                           WHEN document_links.source_document_id = walk.document_id
                               THEN document_links.target_document_id
                           ELSE document_links.source_document_id
                       END,
                       walk.depth + 1
                FROM walk
                JOIN document_links
                  ON document_links.resolution_status = 'resolved'
                 AND (
                    document_links.source_document_id = walk.document_id
                    OR document_links.target_document_id = walk.document_id
                 )
                JOIN documents source
                  ON source.document_id = document_links.source_document_id
                 AND source.deleted_at_ms IS NULL
                JOIN documents target
                  ON target.document_id = document_links.target_document_id
                 AND target.deleted_at_ms IS NULL
                WHERE walk.depth < $depth
            )
            SELECT documents.document_id, documents.title, documents.source_path,
                   documents.revision, MIN(walk.depth), documents.updated_at_ms
            FROM walk
            JOIN documents ON documents.document_id = walk.document_id
            WHERE documents.deleted_at_ms IS NULL
            """);
        AppendTagFilter(sql, tags);
        AppendFolderFilter(sql, normalizedFolder);
        sql.Append(
            """
             GROUP BY documents.document_id, documents.title,
                      documents.source_path, documents.revision,
                      documents.updated_at_ms
             ORDER BY MIN(walk.depth), documents.updated_at_ms DESC,
                      documents.document_id ASC
             LIMIT $limit;
            """);

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$center", centerId);
        command.Parameters.AddWithValue("$depth", depth);
        command.Parameters.AddWithValue("$limit", maxNodes + 1);
        AddTagParameters(command, tags);
        AddFolderParameter(command, normalizedFolder);

        var nodes = new List<GraphNodeRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nodes.Add(new GraphNodeRow(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4)));
            }
        }

        var truncated = nodes.Count > maxNodes;
        if (truncated)
        {
            nodes.RemoveAt(nodes.Count - 1);
        }
        if (nodes.Count == 0)
        {
            return new VaultLocalGraphResult([], [], false, maxNodes, maxEdges);
        }

        var nodeIds = nodes
            .Select(node => node.DocumentId.ToString("D").ToLowerInvariant())
            .ToArray();
        var tagsByNode = await ReadNodeTagsAsync(
            connection,
            nodeIds,
            cancellationToken).ConfigureAwait(false);
        var edges = await ReadEdgesAsync(
            connection,
            nodeIds,
            maxEdges,
            cancellationToken).ConfigureAwait(false);
        if (edges.Count > maxEdges)
        {
            truncated = true;
            edges.RemoveAt(edges.Count - 1);
        }

        var degree = nodeIds.ToDictionary(
            id => id,
            _ => 0,
            StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            var source = edge.SourceDocumentId.ToString("D").ToLowerInvariant();
            var target = edge.TargetDocumentId.ToString("D").ToLowerInvariant();
            degree[source] = degree.GetValueOrDefault(source) + edge.LinkCount;
            degree[target] = degree.GetValueOrDefault(target) + edge.LinkCount;
        }

        return new VaultLocalGraphResult(
            nodes.Select(node => new VaultGraphNode(
                node.DocumentId,
                node.Title,
                node.VirtualPath,
                node.Revision,
                tagsByNode.GetValueOrDefault(node.DocumentId, []),
                node.Depth,
                degree.GetValueOrDefault(
                    node.DocumentId.ToString("D").ToLowerInvariant()),
                node.DocumentId == query.CenterDocumentId))
                .ToArray(),
            edges,
            truncated,
            maxNodes,
            maxEdges);
    }

    private static void AppendTagFilter(
        StringBuilder sql,
        IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }
        sql.Append(
            """
             AND (
                SELECT count(DISTINCT tags.normalized_name)
                FROM document_tags
                JOIN tags ON tags.tag_id = document_tags.tag_id
                WHERE document_tags.document_id = documents.document_id
                  AND tags.normalized_name IN (
            """);
        for (var index = 0; index < tags.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }
            sql.Append("$tag").Append(index);
        }
        sql.Append(")) = $tagCount ");
    }

    private static void AppendFolderFilter(
        StringBuilder sql,
        string normalizedFolder)
    {
        if (normalizedFolder.Length == 0)
        {
            return;
        }
        sql.Append(
            " AND documents.source_path_norm LIKE $folderPrefix ESCAPE '\\' ");
    }

    private static void AddTagParameters(
        SqliteCommand command,
        IReadOnlyList<string> tags)
    {
        for (var index = 0; index < tags.Count; index++)
        {
            command.Parameters.AddWithValue($"$tag{index}", tags[index]);
        }
        if (tags.Count > 0)
        {
            command.Parameters.AddWithValue("$tagCount", tags.Count);
        }
    }

    private static void AddFolderParameter(
        SqliteCommand command,
        string normalizedFolder)
    {
        if (normalizedFolder.Length > 0)
        {
            command.Parameters.AddWithValue(
                "$folderPrefix",
                EscapeLike(normalizedFolder) + "/%");
        }
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> ReadNodeTagsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(
            """
            SELECT document_tags.document_id, tags.name
            FROM document_tags
            JOIN tags ON tags.tag_id = document_tags.tag_id
            WHERE document_tags.document_id IN (
            """);
        AppendPlaceholders(sql, nodeIds.Count, "node");
        sql.Append(") ORDER BY document_tags.document_id, tags.normalized_name;");
        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        AddPlaceholders(command, nodeIds, "node");
        var result = new Dictionary<Guid, List<string>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (!result.TryGetValue(id, out var tags))
            {
                tags = [];
                result[id] = tags;
            }
            tags.Add(reader.GetString(1));
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value);
    }

    private static async Task<List<VaultGraphEdge>> ReadEdgesAsync(
        SqliteConnection connection,
        IReadOnlyList<string> nodeIds,
        int maxEdges,
        CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(
            """
            SELECT document_links.source_document_id,
                   document_links.target_document_id,
                   document_links.relation_type,
                   count(*)
            FROM document_links
            WHERE document_links.resolution_status = 'resolved'
              AND document_links.source_document_id IN (
            """);
        AppendPlaceholders(sql, nodeIds.Count, "source");
        sql.Append(") AND document_links.target_document_id IN (");
        AppendPlaceholders(sql, nodeIds.Count, "target");
        sql.Append(
            """
             )
             GROUP BY document_links.source_document_id,
                      document_links.target_document_id,
                      document_links.relation_type
             ORDER BY document_links.source_document_id,
                      document_links.target_document_id,
                      document_links.relation_type
             LIMIT $limit;
            """);
        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        AddPlaceholders(command, nodeIds, "source");
        AddPlaceholders(command, nodeIds, "target");
        command.Parameters.AddWithValue("$limit", maxEdges + 1);
        var result = new List<VaultGraphEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new VaultGraphEdge(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt32(3)));
        }
        return result;
    }

    private static void AppendPlaceholders(
        StringBuilder sql,
        int count,
        string prefix)
    {
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }
            sql.Append('$').Append(prefix).Append(index);
        }
    }

    private static void AddPlaceholders(
        SqliteCommand command,
        IReadOnlyList<string> values,
        string prefix)
    {
        for (var index = 0; index < values.Count; index++)
        {
            command.Parameters.AddWithValue($"${prefix}{index}", values[index]);
        }
    }

    private static async Task EnsureActiveDocumentAsync(
        SqliteConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM documents WHERE document_id = $id AND deleted_at_ms IS NULL;";
        command.Parameters.AddWithValue("$id", documentId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new VaultException(
                VaultErrorCode.NotFound,
                "Document was not found.");
        }
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private sealed record GraphNodeRow(
        Guid DocumentId,
        string Title,
        string VirtualPath,
        int Revision,
        int Depth);
}
