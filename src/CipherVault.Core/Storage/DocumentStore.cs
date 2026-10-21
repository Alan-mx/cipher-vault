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
        var offset = CursorCodec.Decode(
            cursor,
            sessionId,
            "catalog",
            CursorCodec.Fingerprint(normalizedFolder));

        var sql = new StringBuilder(
            """
            SELECT document_id, title, source_path, format, revision,
                   headings_json, tags_json, updated_at_ms
            FROM documents
            WHERE deleted_at_ms IS NULL
            """);
        if (normalizedFolder.Length > 0)
        {
            sql.Append(" AND source_path_norm LIKE $folderPrefix ESCAPE '\\'");
        }
        sql.Append(" ORDER BY source_path_norm, document_id LIMIT $limit OFFSET $offset;");

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        if (normalizedFolder.Length > 0)
        {
            command.Parameters.AddWithValue("$folderPrefix", EscapeLike(normalizedFolder) + "/%");
        }
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        command.Parameters.AddWithValue("$offset", offset);

        var rows = new List<VaultCatalogItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadCatalogItem(reader));
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
                offset + rows.Count,
                CursorCodec.Fingerprint(normalizedFolder))
            : null;
        return new VaultCatalogPage(rows, nextCursor);
    }

    public static async Task<VaultScanPage> ScanAsync(
        SqliteConnection connection,
        Guid sessionId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 50);
        var offset = CursorCodec.Decode(
            cursor,
            sessionId,
            "scan",
            CursorCodec.Fingerprint(string.Empty));

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT document_id, title, source_path, format, revision,
                   headings_json, tags_json, updated_at_ms
            FROM documents
            WHERE deleted_at_ms IS NULL
            ORDER BY source_path_norm, document_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        command.Parameters.AddWithValue("$offset", offset);

        var rows = new List<VaultCatalogItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadCatalogItem(reader));
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
                offset + rows.Count,
                CursorCodec.Fingerprint(string.Empty))
            : null;
        return new VaultScanPage(rows, nextCursor, !hasMore);
    }

    public static async Task<VaultTrashPage> ListTrashAsync(
        SqliteConnection connection,
        Guid sessionId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 100);
        var offset = CursorCodec.Decode(
            cursor,
            sessionId,
            "trash",
            CursorCodec.Fingerprint(string.Empty));
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT document_id, title, source_path, revision, deleted_at_ms, purge_after_ms
            FROM documents
            WHERE deleted_at_ms IS NOT NULL
            ORDER BY deleted_at_ms DESC, document_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<VaultTrashItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new VaultTrashItem(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
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
                offset + items.Count,
                CursorCodec.Fingerprint(string.Empty))
            : null;
        return new VaultTrashPage(items, nextCursor);
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
        var offset = CursorCodec.Decode(cursor, sessionId, "revisions", fingerprint);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revision_id, document_id, revision, title, source_path,
                   created_at_ms, retain_until_ms
            FROM document_revisions
            WHERE document_id = $id
            ORDER BY revision DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$id", documentIdValue);
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<VaultRevisionItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new VaultRevisionItem(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
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
                offset + items.Count,
                fingerprint)
            : null;
        return new VaultRevisionPage(items, nextCursor);
    }

    public static async Task<VaultAuditPage> ListAuditAsync(
        SqliteConnection connection,
        Guid sessionId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 200);
        var offset = CursorCodec.Decode(
            cursor,
            sessionId,
            "audit",
            CursorCodec.Fingerprint(string.Empty));
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, occurred_at_ms, actor_kind, client_name, action,
                   document_id, result, bytes_returned, details_json
            FROM audit_events
            ORDER BY occurred_at_ms DESC, event_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        var items = new List<VaultAuditItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new VaultAuditItem(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetString(8)));
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
                offset + items.Count,
                CursorCodec.Fingerprint(string.Empty))
            : null;
        return new VaultAuditPage(items, nextCursor);
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
        var offset = CursorCodec.Decode(cursor, sessionId, "search", fingerprint);
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
                offset,
                totalActive,
                cancellationToken).ConfigureAwait(false)
            : await SearchShortAsync(
                connection,
                sessionId,
                normalizedQuery,
                normalizedFolder,
                fingerprint,
                effectiveLimit,
                offset,
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
        int offset,
        int totalActive,
        CancellationToken cancellationToken)
    {
        var folderClause = folder.Length == 0
            ? string.Empty
            : " AND d.source_path_norm LIKE $folderPrefix ESCAPE '\\'";
        var match = $"\"{query.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT d.document_id, d.title, d.source_path, d.revision,
                   snippet(documents_fts, -1, '[', ']', '...', 32) AS snippet,
                   bm25(documents_fts) AS rank
            FROM documents_fts
            JOIN documents d ON d.document_id = documents_fts.document_id
            WHERE documents_fts MATCH $match
              AND d.deleted_at_ms IS NULL
              {folderClause}
            ORDER BY rank, d.updated_at_ms DESC, d.document_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        if (folder.Length > 0)
        {
            command.Parameters.AddWithValue("$folderPrefix", EscapeLike(folder) + "/%");
        }

        var hits = new List<VaultSearchHit>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add(ReadSearchHit(reader));
            }
        }

        var matched = await CountFtsMatchesAsync(
            connection,
            match,
            folder,
            cancellationToken).ConfigureAwait(false);
        return BuildSearchPage(
            hits,
            sessionId,
            fingerprint,
            limit,
            offset,
            totalActive,
            matched);
    }

    private static async Task<VaultSearchPage> SearchShortAsync(
        SqliteConnection connection,
        Guid sessionId,
        string query,
        string folder,
        string fingerprint,
        int limit,
        int offset,
        int totalActive,
        CancellationToken cancellationToken)
    {
        var like = "%" + EscapeLike(query) + "%";
        var folderClause = folder.Length == 0
            ? string.Empty
            : " AND source_path_norm LIKE $folderPrefix ESCAPE '\\'";
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT document_id, title, source_path, revision, content
            FROM documents
            WHERE deleted_at_ms IS NULL
              AND (
                title LIKE $like ESCAPE '\'
                OR source_path LIKE $like ESCAPE '\'
                OR headings_json LIKE $like ESCAPE '\'
                OR tags_json LIKE $like ESCAPE '\'
                OR content LIKE $like ESCAPE '\'
              )
              {folderClause}
            ORDER BY updated_at_ms DESC, document_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$like", like);
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        if (folder.Length > 0)
        {
            command.Parameters.AddWithValue("$folderPrefix", EscapeLike(folder) + "/%");
        }

        var hits = new List<VaultSearchHit>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add(ReadShortSearchHit(reader, query));
            }
        }

        var scanned = Math.Min(totalActive, MaxScanDocuments);
        var matched = Math.Min(scanned, offset + hits.Count);
        return BuildSearchPage(
            hits,
            sessionId,
            fingerprint,
            limit,
            offset,
            scanned,
            matched);
    }

    private static VaultSearchPage BuildSearchPage(
        List<VaultSearchHit> hits,
        Guid sessionId,
        string fingerprint,
        int limit,
        int offset,
        int scanned,
        int matched)
    {
        var hasMore = hits.Count > limit;
        if (hasMore)
        {
            hits.RemoveAt(hits.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                sessionId,
                "search",
                offset + hits.Count,
                fingerprint)
            : null;
        return new VaultSearchPage(
            hits,
            new VaultSearchCoverage(scanned, matched, hits.Count),
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
}
