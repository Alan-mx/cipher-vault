using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeGraph;

namespace CipherVault.Core.Storage.Graph;

public static class KnowledgeGraphStore
{
    private const int HardMaxNodes = 500;
    private const int HardMaxEdges = 1_000;

    public static async Task<LocalGraphResult> GetAsync(
        VaultOperationContext context,
        LocalGraphQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Depth is < 1 or > 3
            || query.MaxNodes is < 1 or > HardMaxNodes
            || query.MaxEdges is < 1 or > HardMaxEdges)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Graph depth or size limits are invalid.");
        }

        var folder = string.IsNullOrWhiteSpace(query.Folder)
            ? string.Empty
            : VirtualPath.NormalizeFolder(query.Folder);
        var tags = TagNormalizer.NormalizeMany(query.Tags)
            .Select(tag => tag.NormalizedKey)
            .ToArray();
        var nodes = await ReadNodesAsync(context, cancellationToken);
        if (!nodes.TryGetValue(query.CenterDocumentId, out _))
        {
            throw new VaultException(
                VaultErrorCode.DocumentNotFound,
                "Graph center document was not found.");
        }
        var edges = await ReadEdgesAsync(context, cancellationToken);
        var adjacency = edges
            .SelectMany(edge => new[]
            {
                (Center: edge.SourceDocumentId, Neighbor: edge.TargetDocumentId),
                (Center: edge.TargetDocumentId, Neighbor: edge.SourceDocumentId)
            })
            .GroupBy(pair => pair.Center)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(pair => pair.Neighbor)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray());

        var selected = new HashSet<Guid> { query.CenterDocumentId };
        var depths = new Dictionary<Guid, int>
        {
            [query.CenterDocumentId] = 0
        };
        var queue = new Queue<Guid>();
        queue.Enqueue(query.CenterDocumentId);
        var truncated = false;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var depth = depths[current];
            if (depth >= query.Depth
                || !adjacency.TryGetValue(current, out var neighbors))
            {
                continue;
            }

            foreach (var neighbor in neighbors)
            {
                if (selected.Contains(neighbor)
                    || !nodes.TryGetValue(neighbor, out var node)
                    || !MatchesFilter(node, tags, folder))
                {
                    continue;
                }
                if (selected.Count >= query.MaxNodes)
                {
                    truncated = true;
                    continue;
                }

                selected.Add(neighbor);
                depths[neighbor] = depth + 1;
                queue.Enqueue(neighbor);
            }
        }

        var selectedEdges = edges
            .Where(edge =>
                selected.Contains(edge.SourceDocumentId)
                && selected.Contains(edge.TargetDocumentId))
            .OrderBy(edge => nodes[edge.SourceDocumentId].Path, StringComparer.Ordinal)
            .ThenBy(edge => nodes[edge.TargetDocumentId].Path, StringComparer.Ordinal)
            .ToArray();
        if (selectedEdges.Length > query.MaxEdges)
        {
            selectedEdges = selectedEdges[..query.MaxEdges];
            truncated = true;
        }

        var degrees = new Dictionary<Guid, int>();
        foreach (var edge in selectedEdges)
        {
            degrees[edge.SourceDocumentId] = degrees.GetValueOrDefault(edge.SourceDocumentId) + 1;
            degrees[edge.TargetDocumentId] = degrees.GetValueOrDefault(edge.TargetDocumentId) + 1;
        }
        var graphNodes = selected
            .Select(id => nodes[id])
            .OrderBy(node => depths[node.DocumentId])
            .ThenBy(node => node.Path, StringComparer.Ordinal)
            .ThenBy(node => node.DocumentId)
            .Select(node => new GraphNode(
                node.DocumentId,
                node.Title,
                node.Path,
                node.Revision,
                node.Tags,
                depths[node.DocumentId],
                degrees.GetValueOrDefault(node.DocumentId),
                node.DocumentId == query.CenterDocumentId))
            .ToArray();
        var graphEdges = selectedEdges
            .Select(edge => new GraphEdge(
                edge.SourceDocumentId,
                edge.TargetDocumentId,
                edge.LinkCount))
            .ToArray();
        return new LocalGraphResult(graphNodes, graphEdges, truncated);
    }

    private static async Task<Dictionary<Guid, GraphDocument>> ReadNodesAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<Guid, GraphDocument>();
        using (var read = context.CreateCommand())
        {
            read.Command.CommandText =
                """
                SELECT document_id, title, path, revision
                FROM documents
                WHERE deleted_at IS NULL;
                """;
            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                nodes[id] = new GraphDocument(
                    id,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    []);
            }
        }

        if (nodes.Count == 0)
        {
            return nodes;
        }

        using (var tagsRead = context.CreateCommand())
        {
            tagsRead.Command.CommandText =
                """
                SELECT dt.document_id, t.normalized_key
                FROM document_tags AS dt
                JOIN tags AS t ON t.tag_id = dt.tag_id
                JOIN documents AS d ON d.document_id = dt.document_id
                WHERE d.deleted_at IS NULL
                ORDER BY dt.document_id, t.normalized_key;
                """;
            await using var reader = await tagsRead.Command.ExecuteReaderAsync(cancellationToken);
            var tagMap = nodes.ToDictionary(
                pair => pair.Key,
                pair => new List<string>());
            while (await reader.ReadAsync(cancellationToken))
            {
                if (tagMap.TryGetValue(
                        Guid.Parse(reader.GetString(0)),
                        out var nodeTags))
                {
                    nodeTags.Add(reader.GetString(1));
                }
            }
            foreach (var pair in tagMap)
            {
                nodes[pair.Key] = nodes[pair.Key] with { Tags = pair.Value.ToArray() };
            }
        }
        return nodes;
    }

    private static async Task<IReadOnlyList<GraphLink>> ReadEdgesAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        var edges = new List<GraphLink>();
        using var read = context.CreateCommand();
        read.Command.CommandText =
            """
            SELECT source_document_id, target_document_id, count(*)
            FROM document_links AS l
            JOIN documents AS source ON source.document_id = l.source_document_id
            JOIN documents AS target ON target.document_id = l.target_document_id
            WHERE l.resolution_status = 'resolved'
              AND source.deleted_at IS NULL
              AND target.deleted_at IS NULL
              AND source.revision = l.source_revision
            GROUP BY source_document_id, target_document_id
            ORDER BY source_document_id, target_document_id;
            """;
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            edges.Add(new GraphLink(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2)));
        }
        return edges;
    }

    private static bool MatchesFilter(
        GraphDocument node,
        IReadOnlyList<string> requiredTags,
        string folder)
    {
        if (requiredTags.Count > 0
            && !requiredTags.All(tag => node.Tags.Contains(tag, StringComparer.Ordinal)))
        {
            return false;
        }
        if (folder.Length > 0
            && !node.Path.StartsWith(folder + "/", StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private sealed record GraphDocument(
        Guid DocumentId,
        string Title,
        string Path,
        int Revision,
        IReadOnlyList<string> Tags);

    private sealed record GraphLink(
        Guid SourceDocumentId,
        Guid TargetDocumentId,
        int LinkCount);
}
