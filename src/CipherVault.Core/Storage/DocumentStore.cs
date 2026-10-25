using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Storage;

internal static partial class DocumentStore
{
    private const int MaxSearchLimit = 20;
    private const int MaxScanDocuments = 1_000;
    private const int MaxSnippetLength = 2_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<VaultCatalogPage> ListCatalogAsync(
        SqliteConnection connection,
        Guid sessionId,
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 100);
        var normalizedFolder = NormalizeFolder(folder);
        var fingerprint = CursorCodec.Fingerprint(normalizedFolder);
        var hasCursor = CursorCodec.TryDecode(
            cursor,
            sessionId,
            "catalog",
            fingerprint,
            out CatalogCursor position);
        var snapshotAtMs = hasCursor
            ? position.SnapshotAtMs
            : await GetMaxUpdatedAtAsync(
                connection,
                normalizedFolder,
                cancellationToken).ConfigureAwait(false);

        var sql = new StringBuilder(
            """
            SELECT document_id, title, source_path, format, revision,
                   headings_json, tags_json, updated_at_ms, source_path_norm
            FROM documents
            WHERE deleted_at_ms IS NULL
              AND updated_at_ms <= $snapshotAt
            """);
        if (normalizedFolder.Length > 0)
        {
            sql.Append(" AND source_path_norm LIKE $folderPrefix ESCAPE '\\'");
        }
        if (hasCursor)
        {
            sql.Append(
                """
                 AND (
                    source_path_norm > $afterPath
                    OR (source_path_norm = $afterPath AND document_id > $afterDocumentId)
                 )
                """);
        }
        sql.Append(" ORDER BY source_path_norm, document_id LIMIT $limit;");

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        if (normalizedFolder.Length > 0)
        {
            command.Parameters.AddWithValue("$folderPrefix", EscapeLike(normalizedFolder) + "/%");
        }
        command.Parameters.AddWithValue("$snapshotAt", snapshotAtMs);
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        if (hasCursor)
        {
            command.Parameters.AddWithValue("$afterPath", position.SourcePathNorm);
            command.Parameters.AddWithValue("$afterDocumentId", position.DocumentId);
        }

        var rows = new List<(VaultCatalogItem Item, CatalogCursor Cursor)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((
                ReadCatalogItem(reader),
                new CatalogCursor(
                    reader.GetString(8),
                    reader.GetString(0),
                    snapshotAtMs)));
        }

        var hasMore = rows.Count > effectiveLimit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                sessionId,
                "catalog",
                fingerprint,
                rows[^1].Cursor)
            : null;
        return new VaultCatalogPage(
            rows.Select(row => row.Item).ToArray(),
            nextCursor);
    }

    public static async Task<VaultScanPage> ScanAsync(
        SqliteConnection connection,
        Guid sessionId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 50);
        var fingerprint = CursorCodec.Fingerprint(string.Empty);
        var hasCursor = CursorCodec.TryDecode(
            cursor,
            sessionId,
            "scan",
            fingerprint,
            out CatalogCursor position);
        var snapshotAtMs = hasCursor
            ? position.SnapshotAtMs
            : await GetMaxUpdatedAtAsync(
                connection,
                string.Empty,
                cancellationToken).ConfigureAwait(false);

        var sql = new StringBuilder(
            """
            SELECT document_id, title, source_path, format, revision,
                   headings_json, tags_json, updated_at_ms, source_path_norm
            FROM documents
            WHERE deleted_at_ms IS NULL
              AND updated_at_ms <= $snapshotAt
            """);
        if (hasCursor)
        {
            sql.Append(
                """
                 AND (
                    source_path_norm > $afterPath
                    OR (source_path_norm = $afterPath AND document_id > $afterDocumentId)
                 )
                """);
        }
        sql.Append(" ORDER BY source_path_norm, document_id LIMIT $limit;");

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$snapshotAt", snapshotAtMs);
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        if (hasCursor)
        {
            command.Parameters.AddWithValue("$afterPath", position.SourcePathNorm);
            command.Parameters.AddWithValue("$afterDocumentId", position.DocumentId);
        }

        var rows = new List<(VaultCatalogItem Item, CatalogCursor Cursor)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((
                ReadCatalogItem(reader),
                new CatalogCursor(
                    reader.GetString(8),
                    reader.GetString(0),
                    snapshotAtMs)));
        }

        var hasMore = rows.Count > effectiveLimit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                sessionId,
                "scan",
                fingerprint,
                rows[^1].Cursor)
            : null;
        return new VaultScanPage(
            rows.Select(row => row.Item).ToArray(),
            nextCursor,
            !hasMore);
    }

    public static async Task<VaultTrashPage> ListTrashAsync(
        SqliteConnection connection,
        Guid sessionId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 100);
        var fingerprint = CursorCodec.Fingerprint(string.Empty);
        var hasCursor = CursorCodec.TryDecode(
            cursor,
            sessionId,
            "trash",
            fingerprint,
            out TrashCursor position);
        var sql = new StringBuilder(
            """
            SELECT document_id, title, source_path, revision, deleted_at_ms, purge_after_ms
            FROM documents
            WHERE deleted_at_ms IS NOT NULL
            """);
        if (hasCursor)
        {
            sql.Append(
                """
                 AND (
                    deleted_at_ms < $afterDeletedAt
                    OR (deleted_at_ms = $afterDeletedAt AND document_id > $afterDocumentId)
                 )
                """);
        }
        sql.Append(" ORDER BY deleted_at_ms DESC, document_id LIMIT $limit;");

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        if (hasCursor)
        {
            command.Parameters.AddWithValue("$afterDeletedAt", position.DeletedAtMs);
            command.Parameters.AddWithValue("$afterDocumentId", position.DocumentId);
        }

        var items = new List<(VaultTrashItem Item, TrashCursor Cursor)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add((
                new VaultTrashItem(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))),
                new TrashCursor(reader.GetInt64(4), reader.GetString(0))));
        }

        var hasMore = items.Count > effectiveLimit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                sessionId,
                "trash",
                fingerprint,
                items[^1].Cursor)
            : null;
        return new VaultTrashPage(
            items.Select(item => item.Item).ToArray(),
            nextCursor);
    }

    public static async Task<VaultRevisionPage> ListRevisionsAsync(
        SqliteConnection connection,
        Guid sessionId,
        Guid documentId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var documentIdValue = documentId.ToString("D").ToLowerInvariant();
        if (!await DocumentExistsAsync(
            connection,
            documentIdValue,
            cancellationToken).ConfigureAwait(false))
        {
            throw new VaultException(VaultErrorCode.NotFound, "Document was not found.");
        }

        var effectiveLimit = Math.Clamp(limit, 1, 100);
        var fingerprint = CursorCodec.Fingerprint(documentIdValue);
        var hasCursor = CursorCodec.TryDecode(
            cursor,
            sessionId,
            "revisions",
            fingerprint,
            out RevisionCursor position);
        var sql = new StringBuilder(
            """
            SELECT revision_id, document_id, revision, title, source_path,
                   created_at_ms, retain_until_ms
            FROM document_revisions
            WHERE document_id = $id
            """);
        if (hasCursor)
        {
            sql.Append(" AND revision < $afterRevision");
        }
        sql.Append(" ORDER BY revision DESC LIMIT $limit;");

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$id", documentIdValue);
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        if (hasCursor)
        {
            command.Parameters.AddWithValue("$afterRevision", position.Revision);
        }

        var items = new List<(VaultRevisionItem Item, RevisionCursor Cursor)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add((
                new VaultRevisionItem(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))),
                new RevisionCursor(reader.GetInt32(2))));
        }

        var hasMore = items.Count > effectiveLimit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                sessionId,
                "revisions",
                fingerprint,
                items[^1].Cursor)
            : null;
        return new VaultRevisionPage(
            items.Select(item => item.Item).ToArray(),
            nextCursor);
    }

    public static async Task<VaultAuditPage> ListAuditAsync(
        SqliteConnection connection,
        Guid sessionId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 200);
        var fingerprint = CursorCodec.Fingerprint(string.Empty);
        var hasCursor = CursorCodec.TryDecode(
            cursor,
            sessionId,
            "audit",
            fingerprint,
            out AuditCursor position);
        var sql = new StringBuilder(
            """
            SELECT event_id, occurred_at_ms, actor_kind, client_name, action,
                   document_id, result, bytes_returned, details_json
            FROM audit_events
            """);
        if (hasCursor)
        {
            sql.Append(
                """
                 WHERE (
                    occurred_at_ms < $afterOccurredAt
                    OR (occurred_at_ms = $afterOccurredAt AND event_id > $afterEventId)
                 )
                """);
        }
        sql.Append(" ORDER BY occurred_at_ms DESC, event_id LIMIT $limit;");

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        if (hasCursor)
        {
            command.Parameters.AddWithValue("$afterOccurredAt", position.OccurredAtMs);
            command.Parameters.AddWithValue("$afterEventId", position.EventId);
        }
        var items = new List<(VaultAuditItem Item, AuditCursor Cursor)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add((
                new VaultAuditItem(
                    Guid.Parse(reader.GetString(0)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.GetString(8)),
                new AuditCursor(reader.GetInt64(1), reader.GetString(0))));
        }
        var hasMore = items.Count > effectiveLimit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                sessionId,
                "audit",
                fingerprint,
                items[^1].Cursor)
            : null;
        return new VaultAuditPage(
            items.Select(item => item.Item).ToArray(),
            nextCursor);
    }

    public static async Task<VaultIntegrityReport> VerifyAsync(
        SqliteConnection connection,
        byte[] integrityKey,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var value = Convert.ToString(
                await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (!string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"sqlite_integrity={value}");
            }
        }

        var activeDocuments = await CountAsync(
            connection,
            "SELECT count(*) FROM documents WHERE deleted_at_ms IS NULL;",
            cancellationToken).ConfigureAwait(false);
        var revisions = await CountAsync(
            connection,
            "SELECT count(*) FROM document_revisions;",
            cancellationToken).ConfigureAwait(false);
        var ftsRows = await CountAsync(
            connection,
            "SELECT count(*) FROM documents_fts;",
            cancellationToken).ConfigureAwait(false);
        if (activeDocuments != ftsRows)
        {
            problems.Add($"fts_count_mismatch={ftsRows}/{activeDocuments}");
        }

        await VerifyDocumentHashesAsync(
            connection,
            integrityKey,
            problems,
            cancellationToken).ConfigureAwait(false);
        await VerifyRevisionHashesAsync(
            connection,
            integrityKey,
            problems,
            cancellationToken).ConfigureAwait(false);
        await VerifyTagIndexAsync(
            connection,
            problems,
            cancellationToken).ConfigureAwait(false);
        await VerifyFtsCopiesAsync(
            connection,
            problems,
            cancellationToken).ConfigureAwait(false);
        await VerifyLinkIndexAsync(
            connection,
            problems,
            cancellationToken).ConfigureAwait(false);
        return new VaultIntegrityReport(
            problems.Count == 0,
            activeDocuments,
            revisions,
            ftsRows,
            problems);
    }

    public static async Task<int> RebuildFtsAsync(
        SqliteConnection connection,
        Guid sessionId,
        VaultActorKind actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = sqliteTransaction;
                delete.CommandText = "DELETE FROM documents_fts;";
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = sqliteTransaction;
                insert.CommandText =
                    """
                    INSERT INTO documents_fts(document_id, title, source_path, headings, tags, content)
                    SELECT document_id, title, source_path, headings_json, tags_json, content
                    FROM documents
                    WHERE deleted_at_ms IS NULL;
                    """;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await AuditWriter.AppendAsync(
                connection,
                sqliteTransaction,
                actor,
                sessionId,
                "rebuild_fts",
                null,
                "success",
                null,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await CountAsync(
                connection,
                "SELECT count(*) FROM documents_fts;",
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<(int DeletedDocuments, int DeletedRevisions)> CleanupExpiredAsync(
        SqliteConnection connection,
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            var nowMs = now.ToUnixTimeMilliseconds();
            using (var removeFts = connection.CreateCommand())
            {
                removeFts.Transaction = sqliteTransaction;
                removeFts.CommandText =
                    """
                    DELETE FROM documents_fts
                    WHERE document_id IN (
                        SELECT document_id
                        FROM documents
                        WHERE deleted_at_ms IS NOT NULL
                          AND purge_after_ms <= $now
                    );
                    """;
                removeFts.Parameters.AddWithValue("$now", nowMs);
                await removeFts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int deletedRevisions;
            using (var revisions = connection.CreateCommand())
            {
                revisions.Transaction = sqliteTransaction;
                revisions.CommandText =
                    "DELETE FROM document_revisions WHERE retain_until_ms <= $now;";
                revisions.Parameters.AddWithValue("$now", nowMs);
                deletedRevisions = await revisions.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            int deletedDocuments;
            using (var documents = connection.CreateCommand())
            {
                documents.Transaction = sqliteTransaction;
                documents.CommandText =
                    """
                    DELETE FROM documents
                    WHERE deleted_at_ms IS NOT NULL
                      AND purge_after_ms <= $now;
                    """;
                documents.Parameters.AddWithValue("$now", nowMs);
                deletedDocuments = await documents.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            using (var cleanupTags = connection.CreateCommand())
            {
                cleanupTags.Transaction = sqliteTransaction;
                cleanupTags.CommandText =
                    """
                    DELETE FROM tags
                    WHERE NOT EXISTS (
                        SELECT 1 FROM document_tags
                        WHERE document_tags.tag_id = tags.tag_id
                    );
                    """;
                await cleanupTags.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var meta = connection.CreateCommand())
            {
                meta.Transaction = sqliteTransaction;
                meta.CommandText =
                    """
                    INSERT INTO vault_meta(key, value)
                    VALUES('last_cleanup_at_ms', $value)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                meta.Parameters.AddWithValue("$value", nowMs.ToString());
                await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (deletedDocuments > 0 || deletedRevisions > 0)
            {
                await AuditWriter.AppendAsync(
                    connection,
                    sqliteTransaction,
                    VaultActorKind.System,
                    sessionId,
                    "cleanup",
                    null,
                    "success",
                    null,
                    JsonSerializer.Serialize(new { deletedDocuments, deletedRevisions }),
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (deletedDocuments, deletedRevisions);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<VaultReadResult> ReadNoteAsync(
        SqliteConnection connection,
        Guid sessionId,
        byte[] integrityKey,
        Guid documentId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || maxChars is < 1 or > 16_000)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Read offset or maxChars is outside the allowed range.");
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT document_id, title, source_path, format, content,
                   headings_json, tags_json, payload_hmac, revision
            FROM documents
            WHERE document_id = $id AND deleted_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$id", documentId.ToString("D").ToLowerInvariant());

        string id;
        string title;
        string sourcePath;
        string format;
        string content;
        string headingsJson;
        string tagsJson;
        byte[] expectedHmac;
        int revision;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new VaultException(VaultErrorCode.NotFound, "Document was not found.");
            }

            id = reader.GetString(0);
            title = reader.GetString(1);
            sourcePath = reader.GetString(2);
            format = reader.GetString(3);
            content = reader.GetString(4);
            headingsJson = reader.GetString(5);
            tagsJson = reader.GetString(6);
            expectedHmac = (byte[])reader["payload_hmac"];
            revision = reader.GetInt32(8);
        }

        bool valid;
        try
        {
            valid = DocumentHmac.Verify(
                integrityKey,
                expectedHmac,
                id,
                revision,
                title,
                sourcePath,
                format,
                headingsJson,
                tagsJson,
                content);
        }
        catch (InvalidOperationException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Document integrity metadata is invalid.",
                exception);
        }
        if (!valid)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Document integrity check failed.");
        }

        var safeOffset = Math.Min(offset, content.Length);
        var length = Math.Min(maxChars, content.Length - safeOffset);
        var chunk = content.Substring(safeOffset, length);
        var nextOffset = safeOffset + length;
        return new VaultReadResult(
            documentId,
            title,
            sourcePath,
            revision,
            chunk,
            safeOffset,
            nextOffset,
            content.Length,
            nextOffset >= content.Length,
            ParseHeadings(headingsJson));
    }

    public static async Task<VaultSearchPage> SearchAsync(
        SqliteConnection connection,
        Guid sessionId,
        string query,
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeQuery(query);
        var effectiveLimit = Math.Clamp(limit, 1, MaxSearchLimit);
        var normalizedFolder = NormalizeFolder(folder);
        var fingerprint = CursorCodec.Fingerprint(
            $"{normalizedQuery}\u001F{normalizedFolder}");
        var totalActive = await CountActiveDocumentsAsync(
            connection,
            normalizedFolder,
            cancellationToken).ConfigureAwait(false);

        return normalizedQuery.Length >= 3
            ? await SearchFtsAsync(
                connection,
                sessionId,
                normalizedQuery,
                normalizedFolder,
                fingerprint,
                effectiveLimit,
                cursor,
                totalActive,
                cancellationToken).ConfigureAwait(false)
            : await SearchShortAsync(
                connection,
                sessionId,
                normalizedQuery,
                normalizedFolder,
                fingerprint,
                effectiveLimit,
                cursor,
                totalActive,
                cancellationToken).ConfigureAwait(false);
    }

    private static async Task<VaultSearchPage> SearchFtsAsync(
        SqliteConnection connection,
        Guid sessionId,
        string query,
        string folder,
        string fingerprint,
        int limit,
        string? cursor,
        int totalActive,
        CancellationToken cancellationToken)
    {
        var hasCursor = CursorCodec.TryDecode(
            cursor,
            sessionId,
            "search",
            fingerprint,
            out FtsSearchCursor position);
        var folderClause = folder.Length == 0
            ? string.Empty
            : " AND d.source_path_norm LIKE $folderPrefix ESCAPE '\\'";
        var match = $"\"{query.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        var cursorClause = hasCursor
            ? """
               WHERE (
                  ranked.updated_at_ms < $afterUpdatedAt
                  OR (
                     ranked.updated_at_ms = $afterUpdatedAt
                     AND ranked.document_id > $afterDocumentId
                  )
               )
              """
            : string.Empty;

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT document_id, title, source_path, revision, snippet, score, updated_at_ms
            FROM (
                SELECT d.document_id, d.title, d.source_path, d.revision,
                       snippet(documents_fts, -1, '[', ']', '...', 32) AS snippet,
                       bm25(documents_fts) AS score,
                       d.updated_at_ms AS updated_at_ms
                FROM documents_fts
                JOIN documents d ON d.document_id = documents_fts.document_id
                WHERE documents_fts MATCH $match
                  AND d.deleted_at_ms IS NULL
                  {folderClause}
            ) ranked
            {cursorClause}
            ORDER BY updated_at_ms DESC, document_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit + 1);
        if (folder.Length > 0)
        {
            command.Parameters.AddWithValue("$folderPrefix", EscapeLike(folder) + "/%");
        }
        if (hasCursor)
        {
            command.Parameters.AddWithValue("$afterUpdatedAt", position.UpdatedAtMs);
            command.Parameters.AddWithValue("$afterDocumentId", position.DocumentId);
        }

        var hits = new List<VaultSearchHit>();
        var cursors = new List<FtsSearchCursor>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add(ReadSearchHit(reader));
                cursors.Add(new FtsSearchCursor(
                    reader.GetInt64(6),
                    reader.GetString(0)));
            }
        }

        var matched = await CountFtsMatchesAsync(
            connection,
            match,
            folder,
            cancellationToken).ConfigureAwait(false);
        return BuildSearchPage(
            hits,
            cursors,
            sessionId,
            fingerprint,
            limit,
            totalActive,
            totalActive,
            matched,
            isTruncated: false);
    }

    private static async Task<VaultSearchPage> SearchShortAsync(
        SqliteConnection connection,
        Guid sessionId,
        string query,
        string folder,
        string fingerprint,
        int limit,
        string? cursor,
        int totalActive,
        CancellationToken cancellationToken)
    {
        var hasCursor = CursorCodec.TryDecode(
            cursor,
            sessionId,
            "search",
            fingerprint,
            out ShortSearchCursor position);
        var like = "%" + EscapeLike(query) + "%";
        var folderClause = folder.Length == 0
            ? string.Empty
            : " AND source_path_norm LIKE $folderPrefix ESCAPE '\\'";
        var scanWindowSql =
            $"""
             SELECT document_id, title, source_path, revision, content,
                    headings_json, tags_json, updated_at_ms
             FROM documents
             WHERE deleted_at_ms IS NULL
               {folderClause}
             ORDER BY updated_at_ms DESC, document_id
             LIMIT $scanLimit
             """;
        var cursorClause = hasCursor
            ? """
               AND (
                  updated_at_ms < $afterUpdatedAt
                  OR (updated_at_ms = $afterUpdatedAt AND document_id > $afterDocumentId)
               )
              """
            : string.Empty;
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            WITH scan_window AS (
                {scanWindowSql}
            )
            SELECT document_id, title, source_path, revision, content, updated_at_ms
            FROM scan_window
            WHERE (
                title LIKE $like ESCAPE '\'
                OR source_path LIKE $like ESCAPE '\'
                OR headings_json LIKE $like ESCAPE '\'
                OR tags_json LIKE $like ESCAPE '\'
                OR content LIKE $like ESCAPE '\'
              )
              {cursorClause}
            ORDER BY updated_at_ms DESC, document_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$like", like);
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$scanLimit", MaxScanDocuments);
        if (folder.Length > 0)
        {
            command.Parameters.AddWithValue("$folderPrefix", EscapeLike(folder) + "/%");
        }
        if (hasCursor)
        {
            command.Parameters.AddWithValue("$afterUpdatedAt", position.UpdatedAtMs);
            command.Parameters.AddWithValue("$afterDocumentId", position.DocumentId);
        }

        var hits = new List<VaultSearchHit>();
        var cursors = new List<ShortSearchCursor>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add(ReadShortSearchHit(reader, query));
                cursors.Add(new ShortSearchCursor(
                    reader.GetInt64(5),
                    reader.GetString(0)));
            }
        }

        var scanned = Math.Min(totalActive, MaxScanDocuments);
        var matched = await CountShortMatchesAsync(
            connection,
            scanWindowSql,
            like,
            folder,
            cancellationToken).ConfigureAwait(false);
        return BuildSearchPage(
            hits,
            cursors,
            sessionId,
            fingerprint,
            limit,
            totalActive,
            scanned,
            matched,
            isTruncated: totalActive > scanned);
    }

    private static VaultSearchPage BuildSearchPage<TCursor>(
        List<VaultSearchHit> hits,
        List<TCursor> cursors,
        Guid sessionId,
        string fingerprint,
        int limit,
        int totalDocuments,
        int scanned,
        int matched,
        bool isTruncated)
    {
        var hasMore = hits.Count > limit;
        if (hasMore)
        {
            hits.RemoveAt(hits.Count - 1);
            cursors.RemoveAt(cursors.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                sessionId,
                "search",
                fingerprint,
                cursors[^1])
            : null;
        return new VaultSearchPage(
            hits,
            new VaultSearchCoverage(
                totalDocuments,
                scanned,
                matched,
                hits.Count,
                isTruncated),
            nextCursor);
    }

    private static VaultCatalogItem ReadCatalogItem(SqliteDataReader reader)
    {
        return new VaultCatalogItem(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            ParseHeadings(reader.GetString(5)),
            ParseStringArray(reader.GetString(6)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)));
    }

    private static VaultSearchHit ReadSearchHit(SqliteDataReader reader)
    {
        var snippet = reader.GetString(4);
        if (snippet.Length > MaxSnippetLength)
        {
            snippet = snippet[..MaxSnippetLength];
        }
        return new VaultSearchHit(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            snippet,
            reader.GetDouble(5));
    }

    private static VaultSearchHit ReadShortSearchHit(SqliteDataReader reader, string query)
    {
        var content = reader.GetString(4);
        var queryIndex = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, queryIndex - 120);
        var length = Math.Min(360, content.Length - start);
        var snippet = content.Substring(start, length);
        return new VaultSearchHit(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            snippet,
            0);
    }

    private static async Task<int> CountActiveDocumentsAsync(
        SqliteConnection connection,
        string folder,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM documents WHERE deleted_at_ms IS NULL";
        if (folder.Length > 0)
        {
            command.CommandText += " AND source_path_norm LIKE $folderPrefix ESCAPE '\\'";
            command.Parameters.AddWithValue("$folderPrefix", EscapeLike(folder) + "/%");
        }
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value);
    }

    private static async Task<long> GetMaxUpdatedAtAsync(
        SqliteConnection connection,
        string folder,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(MAX(updated_at_ms), 0) " +
            "FROM documents WHERE deleted_at_ms IS NULL";
        if (folder.Length > 0)
        {
            command.CommandText +=
                " AND source_path_norm LIKE $folderPrefix ESCAPE '\\'";
            command.Parameters.AddWithValue(
                "$folderPrefix",
                EscapeLike(folder) + "/%");
        }
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value);
    }

    private static async Task VerifyDocumentHashesAsync(
        SqliteConnection connection,
        byte[] integrityKey,
        ICollection<string> problems,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT document_id, title, source_path, format, content,
                   headings_json, tags_json, payload_hmac, revision
            FROM documents
            WHERE deleted_at_ms IS NULL;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var id = reader.GetString(0);
                if (!DocumentHmac.Verify(
                    integrityKey,
                    (byte[])reader["payload_hmac"],
                    id,
                    reader.GetInt32(8),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(4)))
                {
                    problems.Add($"document_hmac_invalid={id}");
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
            {
                problems.Add($"document_metadata_invalid={reader.GetString(0)}");
            }
        }
    }

    private static async Task VerifyRevisionHashesAsync(
        SqliteConnection connection,
        byte[] integrityKey,
        ICollection<string> problems,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revision_id, document_id, revision, title, source_path, format,
                   content, headings_json, tags_json, payload_hmac
            FROM document_revisions;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (!DocumentHmac.Verify(
                    integrityKey,
                    (byte[])reader["payload_hmac"],
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(6)))
                {
                    problems.Add($"revision_hmac_invalid={reader.GetString(0)}");
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
            {
                problems.Add($"revision_metadata_invalid={reader.GetString(0)}");
            }
        }
    }

    private static async Task VerifyTagIndexAsync(
        SqliteConnection connection,
        ICollection<string> problems,
        CancellationToken cancellationToken)
    {
        var storedTagsByDocument =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT document_id, format, content, tags_json
                FROM documents
                WHERE deleted_at_ms IS NULL;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                var format = reader.GetString(1);
                var content = reader.GetString(2);
                var tagsJson = reader.GetString(3);
                if (!TryNormalizeTagsJson(tagsJson, out var storedTags))
                {
                    problems.Add($"document_tags_json_invalid={id}");
                    storedTagsByDocument[id] =
                        new HashSet<string>(StringComparer.Ordinal);
                    continue;
                }
                storedTagsByDocument[id] = storedTags;
                if (format != "md")
                {
                    continue;
                }
                try
                {
                    if (FrontMatterTags.TryExtract(
                            content,
                            out var frontMatterTags) &&
                        !NormalizeTagSet(frontMatterTags).SetEquals(storedTags))
                    {
                        problems.Add($"document_frontmatter_tags_mismatch={id}");
                    }
                }
                catch (VaultException)
                {
                    problems.Add($"document_frontmatter_tags_invalid={id}");
                }
            }
        }

        await VerifyDocumentTagRelationsAsync(
            connection,
            storedTagsByDocument,
            problems,
            cancellationToken).ConfigureAwait(false);
        await VerifyRevisionTagsAsync(
            connection,
            problems,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyDocumentTagRelationsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, HashSet<string>> storedTagsByDocument,
        ICollection<string> problems,
        CancellationToken cancellationToken)
    {
        var relatedTagsByDocument =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT document_tags.document_id, tags.normalized_name
                FROM document_tags
                JOIN tags ON tags.tag_id = document_tags.tag_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                if (!relatedTagsByDocument.TryGetValue(id, out var tags))
                {
                    tags = new HashSet<string>(StringComparer.Ordinal);
                    relatedTagsByDocument[id] = tags;
                }
                tags.Add(reader.GetString(1));
            }
        }

        foreach (var (id, storedTags) in storedTagsByDocument)
        {
            var relatedTags = relatedTagsByDocument.GetValueOrDefault(id) ??
                new HashSet<string>(StringComparer.Ordinal);
            var normalizedStoredTags = storedTags
                .Select(tag => tag.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);
            if (!relatedTags.SetEquals(normalizedStoredTags))
            {
                problems.Add($"document_tag_relation_mismatch={id}");
            }
        }
    }

    private static async Task VerifyFtsCopiesAsync(
        SqliteConnection connection,
        ICollection<string> problems,
        CancellationToken cancellationToken)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT documents.document_id,
                       documents.title, documents.source_path,
                       documents.headings_json, documents.tags_json,
                       documents.content,
                       documents_fts.document_id, documents_fts.title,
                       documents_fts.source_path, documents_fts.headings,
                       documents_fts.tags, documents_fts.content
                FROM documents
                LEFT JOIN documents_fts
                  ON documents_fts.document_id = documents.document_id
                WHERE documents.deleted_at_ms IS NULL;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                if (reader.IsDBNull(6))
                {
                    problems.Add($"fts_row_missing={id}");
                    continue;
                }

                if (!string.Equals(
                        reader.GetString(1),
                        reader.GetString(7),
                        StringComparison.Ordinal))
                {
                    problems.Add($"fts_title_mismatch={id}");
                }
                if (!string.Equals(
                        reader.GetString(2),
                        reader.GetString(8),
                        StringComparison.Ordinal))
                {
                    problems.Add($"fts_path_mismatch={id}");
                }
                if (!string.Equals(
                        reader.GetString(3),
                        reader.GetString(9),
                        StringComparison.Ordinal))
                {
                    problems.Add($"fts_headings_mismatch={id}");
                }
                if (!string.Equals(
                        reader.GetString(4),
                        reader.GetString(10),
                        StringComparison.Ordinal))
                {
                    problems.Add($"fts_tags_mismatch={id}");
                }
                if (!string.Equals(
                        reader.GetString(5),
                        reader.GetString(11),
                        StringComparison.Ordinal))
                {
                    problems.Add($"fts_content_mismatch={id}");
                }
            }
        }

        using var unexpected = connection.CreateCommand();
        unexpected.CommandText =
            """
            SELECT documents_fts.document_id
            FROM documents_fts
            LEFT JOIN documents
              ON documents.document_id = documents_fts.document_id
            WHERE documents.document_id IS NULL
               OR documents.deleted_at_ms IS NOT NULL;
            """;
        await using var unexpectedReader =
            await unexpected.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await unexpectedReader.ReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            problems.Add(
                $"fts_row_unexpected={unexpectedReader.GetString(0)}");
        }
    }

    private static async Task VerifyRevisionTagsAsync(
        SqliteConnection connection,
        ICollection<string> problems,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revision_id, format, content, tags_json
            FROM document_revisions;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var revisionId = reader.GetString(0);
            if (reader.GetString(1) != "md")
            {
                continue;
            }
            if (!TryNormalizeTagsJson(reader.GetString(3), out var storedTags))
            {
                problems.Add($"revision_tags_json_invalid={revisionId}");
                continue;
            }
            try
            {
                if (FrontMatterTags.TryExtract(
                        reader.GetString(2),
                        out var frontMatterTags) &&
                    !NormalizeTagSet(frontMatterTags).SetEquals(storedTags))
                {
                    problems.Add(
                        $"revision_frontmatter_tags_mismatch={revisionId}");
                }
            }
            catch (VaultException)
            {
                problems.Add(
                    $"revision_frontmatter_tags_invalid={revisionId}");
            }
        }
    }

    private static bool TryNormalizeTagsJson(
        string tagsJson,
        out HashSet<string> tags)
    {
        tags = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var values =
                JsonSerializer.Deserialize<List<string>>(tagsJson, JsonOptions) ??
                [];
            tags = NormalizeTagSet(values);
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or VaultException)
        {
            return false;
        }
    }

    private static HashSet<string> NormalizeTagSet(
        IEnumerable<string> tags)
    {
        return tags
            .Select(VirtualPathPolicy.NormalizeTag)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task VerifyLinkIndexAsync(
        SqliteConnection connection,
        ICollection<string> problems,
        CancellationToken cancellationToken)
    {
        var stale = await CountAsync(
            connection,
            """
            SELECT count(*)
            FROM documents
            WHERE links_indexed_revision <> revision;
            """,
            cancellationToken).ConfigureAwait(false);
        if (stale > 0)
        {
            problems.Add($"link_index_stale={stale}");
        }

        var revisionMismatch = await CountAsync(
            connection,
            """
            SELECT count(*)
            FROM document_links
            JOIN documents
              ON documents.document_id = document_links.source_document_id
            WHERE document_links.source_revision <> documents.revision;
            """,
            cancellationToken).ConfigureAwait(false);
        if (revisionMismatch > 0)
        {
            problems.Add($"link_source_revision_mismatch={revisionMismatch}");
        }

        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                problems.Add("foreign_key_check_failed");
            }
        }

        var derivedMismatch = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT document_id, title, source_path, title_norm, link_path_norm
                FROM documents;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(
                        reader.GetString(3),
                        DocumentLinkKeys.NormalizeTitle(reader.GetString(1)),
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        reader.GetString(4),
                        DocumentLinkKeys.NormalizeDocumentPath(reader.GetString(2)),
                        StringComparison.Ordinal))
                {
                    derivedMismatch++;
                }
            }
        }
        if (derivedMismatch > 0)
        {
            problems.Add($"link_key_derived_mismatch={derivedMismatch}");
        }
    }

    private static async Task<bool> DocumentExistsAsync(
        SqliteConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM documents WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is not null;
    }

    private static async Task<int> CountFtsMatchesAsync(
        SqliteConnection connection,
        string match,
        string folder,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*)
            FROM documents_fts
            JOIN documents d ON d.document_id = documents_fts.document_id
            WHERE documents_fts MATCH $match
              AND d.deleted_at_ms IS NULL
            """;
        if (folder.Length > 0)
        {
            command.CommandText += " AND d.source_path_norm LIKE $folderPrefix ESCAPE '\\'";
            command.Parameters.AddWithValue("$folderPrefix", EscapeLike(folder) + "/%");
        }
        command.Parameters.AddWithValue("$match", match);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value);
    }

    private static async Task<int> CountShortMatchesAsync(
        SqliteConnection connection,
        string scanWindowSql,
        string like,
        string folder,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            WITH scan_window AS (
                {scanWindowSql}
            )
            SELECT count(*)
            FROM scan_window
            WHERE (
                title LIKE $like ESCAPE '\'
                OR source_path LIKE $like ESCAPE '\'
                OR headings_json LIKE $like ESCAPE '\'
                OR tags_json LIKE $like ESCAPE '\'
                OR content LIKE $like ESCAPE '\'
            );
            """;
        command.Parameters.AddWithValue("$like", like);
        command.Parameters.AddWithValue("$scanLimit", MaxScanDocuments);
        if (folder.Length > 0)
        {
            command.Parameters.AddWithValue(
                "$folderPrefix",
                EscapeLike(folder) + "/%");
        }
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value);
    }

    private static IReadOnlyList<string> ParseStringArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Document metadata is invalid.",
                exception);
        }
    }

    private static IReadOnlyList<string> ParseHeadings(string json)
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<HeadingItem>>(json, JsonOptions) ?? [];
            return items
                .Select(item => $"H{Math.Clamp(item.Level, 1, 6)}: {item.Text}")
                .ToList();
        }
        catch (JsonException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Document headings are invalid.",
                exception);
        }
    }

    private static string NormalizeQuery(string query)
    {
        var normalized = NormalizeWhitespace(query.Normalize(NormalizationForm.FormC));
        if (normalized.Length is < 1 or > 200)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Search query must contain 1 to 200 characters.");
        }
        return normalized;
    }

    private static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.Empty;
        }

        var normalized = folder
            .Normalize(NormalizationForm.FormC)
            .Replace('\\', '/')
            .Trim('/');
        if (normalized.Split('/').Any(segment => segment is "." or "..") ||
            Path.IsPathRooted(normalized) ||
            normalized.Contains(':'))
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Folder path is invalid.");
        }
        return normalized.ToLowerInvariant();
    }

    private static string NormalizeWhitespace(string value)
    {
        return WhitespaceRegex().Replace(value.Trim(), " ");
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record HeadingItem(int Level, string Text);

    private readonly record struct CatalogCursor(
        string SourcePathNorm,
        string DocumentId,
        long SnapshotAtMs);

    private readonly record struct TrashCursor(
        long DeletedAtMs,
        string DocumentId);

    private readonly record struct RevisionCursor(
        int Revision);

    private readonly record struct AuditCursor(
        long OccurredAtMs,
        string EventId);

    private readonly record struct FtsSearchCursor(
        long UpdatedAtMs,
        string DocumentId);

    private readonly record struct ShortSearchCursor(
        long UpdatedAtMs,
        string DocumentId);
}
