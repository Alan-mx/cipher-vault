using System.Globalization;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Storage.Documents;

public static class DocumentQueryStore
{
    private const int MaxMapHeadings = 500;
    private const int MaxMapFrontMatterFields = 100;
    private const int MaxMapLinks = 500;
    private const int MaxMapCandidates = 50;

    public static async Task<DocumentMetadata> GetMetadataAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(context, documentId, cancellationToken)
            ?? throw NotFound();
        var tags = await ReadTagsAsync(context, [documentId], cancellationToken);
        return metadata with
        {
            Tags = tags.GetValueOrDefault(documentId, [])
        };
    }

    public static async Task<DocumentContentChunk> ReadContentAsync(
        VaultOperationContext context,
        Guid documentId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || maxChars is < 1 or > 65_536)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Content read bounds are invalid.");
        }

        string content;
        int revision;
        using (var read = context.CreateCommand())
        {
            read.Command.CommandText =
                """
                SELECT content, revision
                FROM documents
                WHERE document_id = $documentId AND deleted_at IS NULL;
                """;
            read.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw NotFound();
            }

            content = reader.GetString(0);
            revision = reader.GetInt32(1);
        }

        if (offset > content.Length
            || (offset > 0
                && offset < content.Length
                && char.IsLowSurrogate(content[offset])))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Content offset splits an invalid Unicode boundary.");
        }

        var next = Math.Min(content.Length, checked(offset + maxChars));
        if (next > offset
            && next < content.Length
            && char.IsHighSurrogate(content[next - 1])
            && char.IsLowSurrogate(content[next]))
        {
            next++;
        }

        return new DocumentContentChunk(
            documentId,
            revision,
            content[offset..next],
            offset,
            next,
            content.Length,
            next >= content.Length);
    }

    public static async Task<CatalogPage> ListAsync(
        VaultOperationContext context,
        CatalogQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = NormalizePageSize(query.Page?.PageSize, defaultValue: 20, max: 100);
        var folder = NormalizeFolder(query.Folder);
        var format = query.Format?.ToDatabaseValue();
        var requestedTags = NormalizeCatalogTags(query.Tags);
        var fingerprint = CursorCodec.Fingerprint(
            folder,
            format,
            query.OrphanOnly ? "orphan" : "all",
            string.Join(
                '\u001F',
                requestedTags.Select(tag => tag.NormalizedKey)));
        var hasCursor = CursorCodec.TryDecode(
            query.Page?.Cursor,
            "catalog",
            fingerprint,
            out CatalogCursorPayload? cursor);
        if (query.Page?.Cursor is not null && !hasCursor)
        {
            throw InvalidCursor();
        }

        var snapshotAt = cursor?.SnapshotAt
            ?? now.ToUniversalTime();
        var rows = new List<DocumentMetadata>();
        using (var read = context.CreateCommand())
        {
            var sql = new StringBuilder(
                """
                SELECT document_id, title, path, format, revision, created_at, updated_at
                FROM documents
                WHERE deleted_at IS NULL
                  AND updated_at <= $snapshotAt
                """);
            if (folder.Length > 0)
            {
                sql.Append(
                    " AND substr(path_key, 1, length($folderPrefix)) = $folderPrefix");
            }
            if (format is not null)
            {
                sql.Append(" AND format = $format");
            }
            for (var index = 0; index < requestedTags.Count; index++)
            {
                sql.Append(
                    $"""

                     AND EXISTS (
                         SELECT 1
                         FROM document_tags AS dt{index}
                         JOIN tags AS t{index}
                           ON t{index}.tag_id = dt{index}.tag_id
                         WHERE dt{index}.document_id = documents.document_id
                           AND t{index}.normalized_key = $tag{index}
                     )
                    """);
            }
            if (query.OrphanOnly)
            {
                sql.Append(
                    """

                     AND NOT EXISTS (
                         SELECT 1
                         FROM document_links AS l
                         JOIN documents AS src
                           ON src.document_id = l.source_document_id
                         WHERE l.target_document_id = documents.document_id
                           AND l.resolution_status = 'resolved'
                           AND src.deleted_at IS NULL
                           AND src.document_id <> documents.document_id
                     )
                    """);
            }
            if (cursor is not null)
            {
                sql.Append(
                    """
                     AND (
                        path_key > $afterPath
                        OR (path_key = $afterPath AND document_id > $afterDocumentId)
                     )
                    """);
            }
            sql.Append(" ORDER BY path_key, document_id LIMIT $limit;");
            read.Command.CommandText = sql.ToString();
            read.Command.Parameters.AddWithValue(
                "$snapshotAt",
                snapshotAt.ToString("O"));
            read.Command.Parameters.AddWithValue("$limit", pageSize + 1);
            if (folder.Length > 0)
            {
                read.Command.Parameters.AddWithValue(
                    "$folderPrefix",
                    folder + "/");
            }
            if (format is not null)
            {
                read.Command.Parameters.AddWithValue("$format", format);
            }
            for (var index = 0; index < requestedTags.Count; index++)
            {
                read.Command.Parameters.AddWithValue(
                    $"$tag{index}",
                    requestedTags[index].NormalizedKey);
            }
            if (cursor is not null)
            {
                read.Command.Parameters.AddWithValue("$afterPath", cursor.PathKey);
                read.Command.Parameters.AddWithValue(
                    "$afterDocumentId",
                    cursor.DocumentId.ToString("D"));
            }

            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new DocumentMetadata(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    VaultDocumentFormatExtensions.ParseDatabaseValue(reader.GetString(3)),
                    reader.GetInt32(4),
                    [],
                    ParseTimestamp(reader.GetString(5)),
                    ParseTimestamp(reader.GetString(6))));
            }
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var tags = await ReadTagsAsync(
            context,
            rows.Select(row => row.DocumentId),
            cancellationToken);
        var items = rows
            .Select(row => new CatalogItem(
                row.DocumentId,
                row.Title,
                row.VirtualPath,
                row.Format,
                row.Revision,
                tags[row.DocumentId],
                row.UpdatedAt))
            .ToArray();
        var nextCursor = hasMore && rows.Count > 0
            ? CursorCodec.Encode(
                "catalog",
                fingerprint,
                new CatalogCursorPayload(
                    CanonicalPathKey(rows[^1]),
                    rows[^1].DocumentId,
                    snapshotAt))
            : null;
        return new CatalogPage(items, nextCursor);
    }

    public static async Task<DocumentMap> GetMapAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(context, documentId, cancellationToken);
        string headingsJson;
        string frontMatterJson;
        using (var read = context.CreateCommand())
        {
            read.Command.CommandText =
                """
                SELECT headings_json, frontmatter_json
                FROM documents
                WHERE document_id = $documentId AND deleted_at IS NULL;
                """;
            read.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw NotFound();
            }

            headingsJson = reader.GetString(0);
            frontMatterJson = reader.GetString(1);
        }

        var allHeadings = MarkdownHeadings.ParseWithPaths(headingsJson);
        var headings = allHeadings
            .Take(MaxMapHeadings)
            .Select(heading => new DocumentHeading(
                heading.Level,
                heading.Text,
                heading.Path))
            .ToArray();
        var frontMatterFields = ReadFrontMatterFields(
            frontMatterJson,
            out var frontMatterTruncated);
        var linkRows = await ReadOutgoingLinkRowsAsync(
            context,
            documentId,
            metadata.Revision,
            cancellationToken);
        var targets = await ReadLinkTargetsAsync(
            context,
            linkRows
                .SelectMany(row =>
                    row.CandidateIds.Concat(
                        row.TargetDocumentId is { } targetId ? [targetId] : []))
                .Distinct(),
            cancellationToken);
        var links = new List<OutgoingLinkView>();
        var linksTruncated = linkRows.Count > MaxMapLinks;
        foreach (var row in linkRows.Take(MaxMapLinks))
        {
            var candidateIds = row.CandidateIds.Take(MaxMapCandidates).ToArray();
            linksTruncated |= row.CandidateIds.Count > MaxMapCandidates;
            links.Add(new OutgoingLinkView(
                row.RawTarget,
                row.DisplayText,
                row.TargetHeading,
                row.Status,
                row.TargetDocumentId is { } id && targets.TryGetValue(id, out var target)
                    ? target
                    : null,
                candidateIds
                    .Where(targets.ContainsKey)
                    .Select(id => targets[id])
                    .ToArray(),
                row.StartOffset,
                row.EndOffset));
        }

        var backlinkCount = await ReadBacklinkCountAsync(
            context,
            documentId,
            cancellationToken);
        return new DocumentMap(
            metadata,
            headings,
            frontMatterFields,
            links,
            backlinkCount,
            allHeadings.Count > MaxMapHeadings,
            frontMatterTruncated,
            linksTruncated);
    }

    public static async Task<TagPage> ListTagsAsync(
        VaultOperationContext context,
        CursorPageRequest page,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pageSize = NormalizePageSize(page?.PageSize, 50, 200);
        var fingerprint = CursorCodec.Fingerprint("tags");
        var hasCursor = CursorCodec.TryDecode(
            page?.Cursor,
            "tags",
            fingerprint,
            out TagCursorPayload? cursor);
        if (page?.Cursor is not null && !hasCursor)
        {
            throw InvalidCursor();
        }

        var snapshotAt = cursor?.SnapshotAt ?? now.ToUniversalTime();
        var rows = new List<(string Name, int Count, string Key, Guid Id)>();
        using (var read = context.CreateCommand())
        {
            var sql = new StringBuilder(
                """
                SELECT t.name, count(*), t.normalized_key, t.tag_id
                FROM tags AS t
                JOIN document_tags AS dt ON dt.tag_id = t.tag_id
                JOIN documents AS d ON d.document_id = dt.document_id
                WHERE d.updated_at <= $snapshotAt
                  AND (d.deleted_at IS NULL OR d.deleted_at > $snapshotAt)
                """);
            if (cursor is not null)
            {
                sql.Append(
                    """
                     AND (
                        t.normalized_key > $afterKey
                        OR (
                            t.normalized_key = $afterKey
                            AND t.tag_id > $afterTagId
                        )
                     )
                    """);
            }
            sql.Append(
                """
                 GROUP BY t.tag_id, t.name, t.normalized_key
                 ORDER BY t.normalized_key, t.tag_id
                 LIMIT $limit;
                """);
            read.Command.CommandText = sql.ToString();
            read.Command.Parameters.AddWithValue(
                "$snapshotAt",
                snapshotAt.ToString("O"));
            read.Command.Parameters.AddWithValue("$limit", pageSize + 1);
            if (cursor is not null)
            {
                read.Command.Parameters.AddWithValue("$afterKey", cursor.NormalizedKey);
                read.Command.Parameters.AddWithValue(
                    "$afterTagId",
                    cursor.TagId.ToString("D"));
            }

            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    Guid.Parse(reader.GetString(3))));
            }
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                "tags",
                fingerprint,
                new TagCursorPayload(
                    rows[^1].Key,
                    rows[^1].Id,
                    snapshotAt))
            : null;
        return new TagPage(
            rows.Select(row => new TagItem(row.Name, row.Count)).ToArray(),
            nextCursor);
    }

    public static async Task<BacklinkPage> ListBacklinksAsync(
        VaultOperationContext context,
        Guid documentId,
        CursorPageRequest page,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _ = await GetMetadataAsync(context, documentId, cancellationToken);
        var pageSize = NormalizePageSize(page?.PageSize, 20, 100);
        var fingerprint = CursorCodec.Fingerprint("backlinks", documentId.ToString("D"));
        var hasCursor = CursorCodec.TryDecode(
            page?.Cursor,
            "backlinks",
            fingerprint,
            out BacklinkCursorPayload? cursor);
        if (page?.Cursor is not null && !hasCursor)
        {
            throw InvalidCursor();
        }
        var snapshotAt = cursor?.SnapshotAt ?? now.ToUniversalTime();

        var rows = new List<(BacklinkItem Item, Guid Id)>();
        using (var read = context.CreateCommand())
        {
            var sql = new StringBuilder(
                """
                SELECT d.document_id, d.title, d.path, d.revision,
                       count(*) AS occurrences,
                       min(l.target_heading),
                       d.updated_at
                FROM document_links AS l
                JOIN documents AS d ON d.document_id = l.source_document_id
                WHERE l.target_document_id = $targetId
                  AND l.resolution_status = 'resolved'
                  AND d.deleted_at IS NULL
                  AND d.updated_at <= $snapshotAt
                  AND d.revision = l.source_revision
                """);
            if (cursor is not null)
            {
                sql.Append(
                    """
                     AND (
                        d.updated_at < $afterUpdated
                        OR (
                            d.updated_at = $afterUpdated
                            AND d.document_id < $afterDocumentId
                        )
                     )
                    """);
            }
            sql.Append(
                """
                 GROUP BY d.document_id, d.title, d.path, d.revision, d.updated_at
                 ORDER BY d.updated_at DESC, d.document_id DESC
                 LIMIT $limit;
                """);
            read.Command.CommandText = sql.ToString();
            read.Command.Parameters.AddWithValue(
                "$targetId",
                documentId.ToString("D"));
            read.Command.Parameters.AddWithValue(
                "$snapshotAt",
                snapshotAt.ToString("O"));
            read.Command.Parameters.AddWithValue("$limit", pageSize + 1);
            if (cursor is not null)
            {
                read.Command.Parameters.AddWithValue(
                    "$afterUpdated",
                    cursor.UpdatedAt.ToString("O"));
                read.Command.Parameters.AddWithValue(
                    "$afterDocumentId",
                    cursor.DocumentId.ToString("D"));
            }
            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                rows.Add((
                    new BacklinkItem(
                        id,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        ParseTimestamp(reader.GetString(6))),
                    id));
            }
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                "backlinks",
                fingerprint,
                new BacklinkCursorPayload(
                    rows[^1].Item.UpdatedAt,
                    rows[^1].Id,
                    snapshotAt))
            : null;
        return new BacklinkPage(
            rows.Select(row => row.Item).ToArray(),
            nextCursor);
    }

    public static async Task<UnlinkedMentionPage> ListUnlinkedMentionsAsync(
        VaultOperationContext context,
        Guid targetDocumentId,
        CursorPageRequest page,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var target = await GetMetadataAsync(context, targetDocumentId, cancellationToken);
        var pageSize = NormalizePageSize(page?.PageSize, 20, 100);
        var fingerprint = CursorCodec.Fingerprint(
            "mentions",
            targetDocumentId.ToString("D"));
        var hasCursor = CursorCodec.TryDecode(
            page?.Cursor,
            "mentions",
            fingerprint,
            out MentionCursorPayload? cursor);
        if (page?.Cursor is not null && !hasCursor)
        {
            throw InvalidCursor();
        }
        var snapshotAt = cursor?.SnapshotAt ?? now.ToUniversalTime();

        var results = new List<UnlinkedMentionItem>();
        using (var read = context.CreateCommand())
        {
            var sql = new StringBuilder(
                """
                SELECT document_id, title, path, revision, content
                FROM documents
                WHERE deleted_at IS NULL
                  AND updated_at <= $snapshotAt
                  AND document_id <> $targetId
                """);
            if (cursor is not null)
            {
                sql.Append(
                    """
                     AND (
                        path_key > $afterPath
                        OR (path_key = $afterPath AND document_id > $afterDocumentId)
                     )
                    """);
            }
            sql.Append(" ORDER BY path_key, document_id;");
            read.Command.CommandText = sql.ToString();
            read.Command.Parameters.AddWithValue(
                "$snapshotAt",
                snapshotAt.ToString("O"));
            read.Command.Parameters.AddWithValue(
                "$targetId",
                targetDocumentId.ToString("D"));
            if (cursor is not null)
            {
                read.Command.Parameters.AddWithValue("$afterPath", cursor.PathKey);
                read.Command.Parameters.AddWithValue(
                    "$afterDocumentId",
                    cursor.DocumentId.ToString("D"));
            }

            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)
                   && results.Count <= pageSize)
            {
                var scan = UnlinkedMentionScanner.Scan(
                    reader.GetString(4),
                    target.Title,
                    maxOccurrences: 20);
                if (scan.OccurrenceCount == 0)
                {
                    continue;
                }

                results.Add(new UnlinkedMentionItem(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    scan.OccurrenceCount,
                    scan.Occurrences
                        .Select(item => new UnlinkedMentionOccurrence(
                            item.StartOffset,
                            item.EndOffset,
                            item.Context))
                        .ToArray()));
            }
        }

        var hasMore = results.Count > pageSize;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                "mentions",
                fingerprint,
                new MentionCursorPayload(
                    results[^1].SourceVirtualPath,
                    results[^1].SourceDocumentId,
                    snapshotAt))
            : null;
        return new UnlinkedMentionPage(
            targetDocumentId,
            results,
            nextCursor);
    }

    private static async Task<DocumentMetadata?> ReadMetadataAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        using var read = context.CreateCommand();
        read.Command.CommandText =
            """
            SELECT document_id, title, path, format, revision, created_at, updated_at
            FROM documents
            WHERE document_id = $documentId AND deleted_at IS NULL;
            """;
        read.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DocumentMetadata(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            VaultDocumentFormatExtensions.ParseDatabaseValue(reader.GetString(3)),
            reader.GetInt32(4),
            [],
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)));
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> ReadTagsAsync(
        VaultOperationContext context,
        IEnumerable<Guid> documentIds,
        CancellationToken cancellationToken)
    {
        var ids = documentIds.Distinct().ToArray();
        var result = ids.ToDictionary(
            id => id,
            _ => new List<string>());
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        using var read = context.CreateCommand();
        var parameters = string.Join(
            ", ",
            ids.Select((_, index) => $"$id{index}"));
        read.Command.CommandText =
            $"""
             SELECT dt.document_id, t.name
             FROM document_tags AS dt
             JOIN tags AS t ON t.tag_id = dt.tag_id
             WHERE dt.document_id IN ({parameters})
             ORDER BY dt.document_id, t.normalized_key;
             """;
        for (var index = 0; index < ids.Length; index++)
        {
            read.Command.Parameters.AddWithValue(
                $"$id{index}",
                ids[index].ToString("D"));
        }

        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
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
            pair => pair.Value.Count == 0
                ? (IReadOnlyList<string>)new List<string>()
                : pair.Value.ToArray());
    }

    private static IReadOnlyList<FrontMatterFieldView> ReadFrontMatterFields(
        string json,
        out bool truncated)
    {
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement
            .EnumerateObject()
            .Take(MaxMapFrontMatterFields + 1)
            .Select(property => new FrontMatterFieldView(
                property.Name,
                property.Value.ValueKind switch
                {
                    JsonValueKind.String => "string",
                    JsonValueKind.Number => "number",
                    JsonValueKind.True or JsonValueKind.False => "boolean",
                    JsonValueKind.Array => "array",
                    JsonValueKind.Object => "object",
                    JsonValueKind.Null => "null",
                    _ => "unknown"
                },
                property.Value.GetRawText()))
            .ToArray();
        truncated = fields.Length > MaxMapFrontMatterFields;
        return fields.Take(MaxMapFrontMatterFields).ToArray();
    }

    private static async Task<IReadOnlyList<OutgoingLinkRow>> ReadOutgoingLinkRowsAsync(
        VaultOperationContext context,
        Guid documentId,
        int revision,
        CancellationToken cancellationToken)
    {
        var result = new List<OutgoingLinkRow>();
        using var read = context.CreateCommand();
        read.Command.CommandText =
            """
            SELECT target_document_id, raw_target, display_text, target_heading,
                   resolution_status, candidate_document_ids_json,
                   start_offset, end_offset
            FROM document_links
            WHERE source_document_id = $documentId
              AND source_revision = $revision
            ORDER BY start_offset
            LIMIT $limit;
            """;
        read.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        read.Command.Parameters.AddWithValue("$revision", revision);
        read.Command.Parameters.AddWithValue("$limit", MaxMapLinks + 1);
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var candidateJson = reader.GetString(5);
            var candidates = JsonSerializer.Deserialize<string[]>(candidateJson) ?? [];
            result.Add(new OutgoingLinkRow(
                reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                candidates
                    .Select(Guid.Parse)
                    .ToArray(),
                reader.GetInt32(6),
                reader.GetInt32(7)));
        }
        return result;
    }

    private static async Task<Dictionary<Guid, LinkTargetView>> ReadLinkTargetsAsync(
        VaultOperationContext context,
        IEnumerable<Guid> targetIds,
        CancellationToken cancellationToken)
    {
        var ids = targetIds.Distinct().ToArray();
        var result = new Dictionary<Guid, LinkTargetView>();
        if (ids.Length == 0)
        {
            return result;
        }

        using var read = context.CreateCommand();
        var placeholders = string.Join(
            ", ",
            ids.Select((_, index) => $"$id{index}"));
        read.Command.CommandText =
            $"""
             SELECT document_id, title, path
             FROM documents
             WHERE deleted_at IS NULL AND document_id IN ({placeholders});
             """;
        for (var index = 0; index < ids.Length; index++)
        {
            read.Command.Parameters.AddWithValue(
                $"$id{index}",
                ids[index].ToString("D"));
        }
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            result[id] = new LinkTargetView(
                id,
                reader.GetString(1),
                reader.GetString(2));
        }
        return result;
    }

    private static async Task<int> ReadBacklinkCountAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        using var read = context.CreateCommand();
        read.Command.CommandText =
            """
            SELECT count(*)
            FROM document_links AS l
            JOIN documents AS d ON d.document_id = l.source_document_id
            WHERE l.target_document_id = $documentId
              AND l.resolution_status = 'resolved'
              AND d.deleted_at IS NULL
              AND d.revision = l.source_revision;
            """;
        read.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        return Convert.ToInt32(await read.Command.ExecuteScalarAsync(cancellationToken));
    }

    private static string CanonicalPathKey(DocumentMetadata metadata) =>
        metadata.VirtualPath;

    private static int NormalizePageSize(int? value, int defaultValue, int max)
    {
        var pageSize = value ?? defaultValue;
        if (pageSize is < 1 || pageSize > max)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Page size must be between 1 and {max}.");
        }
        return pageSize;
    }

    private static IReadOnlyList<NormalizedTag> NormalizeCatalogTags(
        IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }
        if (values.Count > TagNormalizer.MaxTags)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"At most {TagNormalizer.MaxTags} catalog tags may be requested.");
        }

        var result = new List<NormalizedTag>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var normalized = TagNormalizer.Normalize(value);
            if (!keys.Add(normalized.NormalizedKey))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Catalog tags must not contain duplicate normalized values.");
            }
            result.Add(normalized);
        }
        return result
            .OrderBy(tag => tag.NormalizedKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.Empty;
        }

        return VirtualPath.NormalizeFolder(folder);
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static VaultException NotFound() =>
        new(VaultErrorCode.DocumentNotFound, "Document was not found.");

    private static VaultException InvalidCursor() =>
        new(VaultErrorCode.InvalidCursor, "Cursor is invalid for this query.");

    private sealed record CatalogCursorPayload(
        string PathKey,
        Guid DocumentId,
        DateTimeOffset SnapshotAt);

    private sealed record TagCursorPayload(
        string NormalizedKey,
        Guid TagId,
        DateTimeOffset SnapshotAt);

    private sealed record BacklinkCursorPayload(
        DateTimeOffset UpdatedAt,
        Guid DocumentId,
        DateTimeOffset SnapshotAt);

    private sealed record MentionCursorPayload(
        string PathKey,
        Guid DocumentId,
        DateTimeOffset SnapshotAt);

    private sealed record OutgoingLinkRow(
        Guid? TargetDocumentId,
        string RawTarget,
        string DisplayText,
        string? TargetHeading,
        string Status,
        IReadOnlyList<Guid> CandidateIds,
        int StartOffset,
        int EndOffset);
}
