using System.Globalization;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Errors;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage.Drafts;

public static class DocumentDraftStore
{
    public static async Task InsertAsync(
        VaultOperationContext context,
        DocumentDraftRecord draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);

        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO document_drafts(
                draft_id,
                draft_revision,
                draft_type,
                status,
                target_document_id,
                base_document_revision,
                payload_version,
                payload_json,
                base_snapshot_json,
                source_actor,
                created_at,
                updated_at,
                terminal_at,
                applied_document_id,
                applied_document_revision,
                applied_at)
            VALUES(
                $draftId,
                $draftRevision,
                $draftType,
                $status,
                $targetDocumentId,
                $baseDocumentRevision,
                $payloadVersion,
                $payloadJson,
                $baseSnapshotJson,
                $sourceActor,
                $createdAt,
                $updatedAt,
                $terminalAt,
                $appliedDocumentId,
                $appliedDocumentRevision,
                $appliedAt);
            """;
        AddParameters(command.Command, draft);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<DocumentDraftRecord> GetAsync(
        VaultOperationContext context,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                draft_id,
                draft_revision,
                draft_type,
                status,
                target_document_id,
                base_document_revision,
                payload_version,
                payload_json,
                base_snapshot_json,
                source_actor,
                created_at,
                updated_at,
                terminal_at,
                applied_document_id,
                applied_document_revision,
                applied_at
            FROM document_drafts
            WHERE draft_id = $draftId;
            """;
        command.Command.Parameters.AddWithValue(
            "$draftId",
            draftId.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw DraftNotFound();
        }

        return ReadRecord(reader);
    }

    public static async Task<DocumentDraftRecord?> FindAsync(
        VaultOperationContext context,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetAsync(context, draftId, cancellationToken);
        }
        catch (VaultException exception)
            when (exception.Code == VaultErrorCode.DraftNotFound)
        {
            return null;
        }
    }

    public static async Task<IReadOnlySet<Guid>> ReadAvailableDocumentIdsAsync(
        VaultOperationContext context,
        IReadOnlyCollection<Guid> documentIds,
        CancellationToken cancellationToken)
    {
        var ids = documentIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new HashSet<Guid>();
        }

        using var command = context.CreateCommand();
        var parameters = string.Join(
            ", ",
            ids.Select((_, index) => $"$id{index}"));
        command.Command.CommandText =
            $"""
             SELECT document_id
             FROM documents
             WHERE deleted_at IS NULL
               AND document_id IN ({parameters});
             """;
        for (var index = 0; index < ids.Length; index++)
        {
            command.Command.Parameters.AddWithValue(
                $"$id{index}",
                ids[index].ToString("D"));
        }
        var available = new HashSet<Guid>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            available.Add(Guid.Parse(reader.GetString(0)));
        }
        return available;
    }

    public static async Task<DocumentDraftRecord> UpdateAsync(
        VaultOperationContext context,
        int expectedDraftRevision,
        DocumentDraftRecord draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.DraftRevision != expectedDraftRevision + 1)
        {
            throw new ArgumentException(
                "The next draft revision must be exactly one greater.",
                nameof(draft));
        }

        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            UPDATE document_drafts
            SET
                draft_revision = $draftRevision,
                status = $status,
                base_document_revision = $baseDocumentRevision,
                payload_version = $payloadVersion,
                payload_json = $payloadJson,
                base_snapshot_json = $baseSnapshotJson,
                updated_at = $updatedAt,
                terminal_at = $terminalAt,
                applied_document_id = $appliedDocumentId,
                applied_document_revision = $appliedDocumentRevision,
                applied_at = $appliedAt
            WHERE draft_id = $draftId
              AND draft_revision = $expectedDraftRevision;
            """;
        AddParameters(command.Command, draft);
        command.Command.Parameters.AddWithValue(
            "$expectedDraftRevision",
            expectedDraftRevision);
        var affected =
            await command.Command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 1)
        {
            return draft;
        }

        var current = await GetAsync(context, draft.DraftId, cancellationToken);
        throw DraftRevisionConflict(current.DraftRevision);
    }

    public static async Task<DocumentDraftQueueSummary> GetSummaryAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                coalesce(sum(
                    CASE WHEN status = 'pending_review' THEN 1 ELSE 0 END
                ), 0),
                coalesce(sum(
                    CASE WHEN status IN ('draft', 'conflict') THEN 1 ELSE 0 END
                ), 0),
                coalesce(sum(
                    CASE WHEN status IN (
                        'applied',
                        'rejected',
                        'discarded',
                        'expired'
                    ) THEN 1 ELSE 0 END
                ), 0)
            FROM document_drafts;
            """;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new DocumentDraftQueueSummary(0, 0, 0);
        }
        return new DocumentDraftQueueSummary(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
    }

    public static async Task<DocumentDraftRecordPage> ListAsync(
        VaultOperationContext context,
        DocumentDraftQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = NormalizePageSize(query.Page.PageSize);
        var queueStatuses = QueueStatuses(query.Queue);
        if (query.Status is { } filterStatus
            && !queueStatuses.Contains(filterStatus))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Draft status does not belong to the selected queue.");
        }
        if (query.SourceActor is { } sourceActor
            && sourceActor is not (VaultActorKind.UserUi or VaultActorKind.Mcp))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Draft source actor must be user_ui or mcp.");
        }
        if (query.UpdatedAfter is { } updatedAfter
            && query.UpdatedBefore is { } updatedBefore
            && updatedAfter >= updatedBefore)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Draft updatedAfter must be earlier than updatedBefore.");
        }
        var sourceActorValue = query.SourceActor switch
        {
            VaultActorKind.UserUi => "user_ui",
            VaultActorKind.Mcp => "mcp",
            _ => null
        };
        var fingerprint = CursorCodec.Fingerprint(
            "document-drafts",
            query.Queue.ToString(),
            query.Status?.ToDatabaseValue(),
            query.Type?.ToDatabaseValue(),
            sourceActorValue,
            query.TargetDocumentId?.ToString("D"),
            query.UpdatedAfter?.ToUniversalTime().ToString("O"),
            query.UpdatedBefore?.ToUniversalTime().ToString("O"),
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            query.Page.Cursor,
            "document-drafts",
            fingerprint,
            out DraftCursorPayload? cursor);
        if (query.Page.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Draft cursor is invalid for this query.");
        }

        var snapshotAt = cursor?.SnapshotAt ?? now.ToUniversalTime();
        using var command = context.CreateCommand();
        var sql = new System.Text.StringBuilder(
            """
            SELECT
                draft_id,
                draft_revision,
                draft_type,
                status,
                target_document_id,
                base_document_revision,
                payload_version,
                payload_json,
                base_snapshot_json,
                source_actor,
                created_at,
                updated_at,
                terminal_at,
                applied_document_id,
                applied_document_revision,
                applied_at
            FROM document_drafts
            WHERE updated_at <= $snapshotAt
            """);
        if (query.Status is { } status)
        {
            sql.Append(" AND status = $status");
            command.Command.Parameters.AddWithValue(
                "$status",
                status.ToDatabaseValue());
        }
        else
        {
            sql.Append(" AND status IN (");
            for (var index = 0; index < queueStatuses.Count; index++)
            {
                if (index > 0)
                {
                    sql.Append(", ");
                }
                var parameter = $"$queueStatus{index}";
                sql.Append(parameter);
                command.Command.Parameters.AddWithValue(
                    parameter,
                    queueStatuses[index].ToDatabaseValue());
            }
            sql.Append(')');
        }
        if (query.Type is { } type)
        {
            sql.Append(" AND draft_type = $draftType");
            command.Command.Parameters.AddWithValue(
                "$draftType",
                type.ToDatabaseValue());
        }
        if (query.TargetDocumentId is { } targetDocumentId)
        {
            sql.Append(" AND target_document_id = $targetDocumentId");
            command.Command.Parameters.AddWithValue(
                "$targetDocumentId",
                targetDocumentId.ToString("D"));
        }
        if (sourceActorValue is not null)
        {
            sql.Append(" AND source_actor = $sourceActor");
            command.Command.Parameters.AddWithValue(
                "$sourceActor",
                sourceActorValue);
        }
        if (query.UpdatedAfter is { } afterBound)
        {
            sql.Append(" AND updated_at >= $updatedAfter");
            command.Command.Parameters.AddWithValue(
                "$updatedAfter",
                afterBound.ToUniversalTime().ToString("O"));
        }
        if (query.UpdatedBefore is { } beforeBound)
        {
            sql.Append(" AND updated_at < $updatedBefore");
            command.Command.Parameters.AddWithValue(
                "$updatedBefore",
                beforeBound.ToUniversalTime().ToString("O"));
        }
        if (cursor is not null)
        {
            sql.Append(
                """
                 AND (
                    updated_at < $afterUpdatedAt
                    OR (
                        updated_at = $afterUpdatedAt
                        AND draft_id < $afterDraftId
                    )
                 )
                """);
            command.Command.Parameters.AddWithValue(
                "$afterUpdatedAt",
                cursor.UpdatedAt.ToUniversalTime().ToString("O"));
            command.Command.Parameters.AddWithValue(
                "$afterDraftId",
                cursor.DraftId.ToString("D"));
        }
        sql.Append(
            """
             ORDER BY updated_at DESC, draft_id DESC
             LIMIT $limit;
            """);
        command.Command.CommandText = sql.ToString();
        command.Command.Parameters.AddWithValue(
            "$snapshotAt",
            snapshotAt.ToString("O"));
        command.Command.Parameters.AddWithValue("$limit", pageSize + 1);

        var rows = new List<DocumentDraftRecord>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadRecord(reader));
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                "document-drafts",
                fingerprint,
                new DraftCursorPayload(
                    rows[^1].UpdatedAt,
                    rows[^1].DraftId,
                    snapshotAt))
            : null;
        return new DocumentDraftRecordPage(rows, nextCursor);
    }

    private static IReadOnlyList<DocumentDraftStatus> QueueStatuses(
        DocumentDraftQueue queue) =>
        queue switch
        {
            DocumentDraftQueue.PendingReview =>
                [DocumentDraftStatus.PendingReview],
            DocumentDraftQueue.Working =>
                [DocumentDraftStatus.Draft, DocumentDraftStatus.Conflict],
            DocumentDraftQueue.History =>
            [
                DocumentDraftStatus.Applied,
                DocumentDraftStatus.Rejected,
                DocumentDraftStatus.Discarded,
                DocumentDraftStatus.Expired
            ],
            _ => throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Draft queue is invalid.")
        };

    public static async Task<int> CountNonTerminalAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT count(*)
            FROM document_drafts
            WHERE status IN ('draft', 'pending_review', 'conflict');
            """;
        return Convert.ToInt32(
            await command.Command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public static async Task<long> SumNonTerminalBytesAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT coalesce(sum(
                length(CAST(payload_json AS BLOB))
                + coalesce(length(CAST(base_snapshot_json AS BLOB)), 0)
            ), 0)
            FROM document_drafts
            WHERE status IN ('draft', 'pending_review', 'conflict');
            """;
        return Convert.ToInt64(
            await command.Command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public static async Task<int> DeleteExpiredTerminalAsync(
        VaultOperationContext context,
        DateTimeOffset terminalBefore,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            DELETE FROM document_drafts
            WHERE terminal_at IS NOT NULL
              AND terminal_at <= $terminalBefore;
            """;
        command.Command.Parameters.AddWithValue(
            "$terminalBefore",
            terminalBefore.ToUniversalTime().ToString("O"));
        return await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<int> DeleteByIdsAsync(
        VaultOperationContext context,
        IReadOnlyCollection<Guid> draftIds,
        CancellationToken cancellationToken)
    {
        if (draftIds.Count == 0)
        {
            return 0;
        }

        using var command = context.CreateCommand();
        var parameters = new List<string>(draftIds.Count);
        var index = 0;
        foreach (var draftId in draftIds)
        {
            var name = $"$draftId{index++}";
            parameters.Add(name);
            command.Command.Parameters.AddWithValue(
                name,
                draftId.ToString("D"));
        }
        command.Command.CommandText =
            $"DELETE FROM document_drafts WHERE draft_id IN ({string.Join(", ", parameters)});";
        return await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(
        SqliteCommand command,
        DocumentDraftRecord draft)
    {
        command.Parameters.AddWithValue(
            "$draftId",
            draft.DraftId.ToString("D"));
        command.Parameters.AddWithValue(
            "$draftRevision",
            draft.DraftRevision);
        command.Parameters.AddWithValue(
            "$draftType",
            draft.DraftType.ToDatabaseValue());
        command.Parameters.AddWithValue(
            "$status",
            draft.Status.ToDatabaseValue());
        command.Parameters.AddWithValue(
            "$targetDocumentId",
            draft.TargetDocumentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseDocumentRevision",
            draft.BaseDocumentRevision ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$payloadVersion",
            draft.PayloadVersion);
        command.Parameters.AddWithValue(
            "$payloadJson",
            draft.PayloadJson);
        command.Parameters.AddWithValue(
            "$baseSnapshotJson",
            draft.BaseSnapshotJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$sourceActor",
            draft.SourceActor);
        command.Parameters.AddWithValue(
            "$createdAt",
            draft.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$updatedAt",
            draft.UpdatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$terminalAt",
            draft.TerminalAt?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$appliedDocumentId",
            draft.AppliedDocumentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$appliedDocumentRevision",
            draft.AppliedDocumentRevision ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$appliedAt",
            draft.AppliedAt?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
    }

    private static DocumentDraftRecord ReadRecord(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            DocumentDraftValueMapping.ParseDraftType(reader.GetString(2)),
            DocumentDraftValueMapping.ParseDraftStatus(reader.GetString(3)),
            reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            ParseTimestamp(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)),
            reader.IsDBNull(12) ? null : ParseTimestamp(reader.GetString(12)),
            reader.IsDBNull(13) ? null : Guid.Parse(reader.GetString(13)),
            reader.IsDBNull(14) ? null : reader.GetInt32(14),
            reader.IsDBNull(15) ? null : ParseTimestamp(reader.GetString(15)));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static int NormalizePageSize(int? value)
    {
        var pageSize = value ?? 50;
        if (pageSize is < 1 or > 200)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Draft page size must be between 1 and 200.");
        }
        return pageSize;
    }

    private static VaultException DraftNotFound() =>
        new(
            VaultErrorCode.DraftNotFound,
            "Document draft was not found.");

    private static VaultException DraftRevisionConflict(int currentRevision) =>
        new(
            VaultErrorCode.DraftRevisionConflict,
            "Document draft revision changed.",
            details: new Dictionary<string, object?>
            {
                ["currentDraftRevision"] = currentRevision
            });

    private sealed record DraftCursorPayload(
        DateTimeOffset UpdatedAt,
        Guid DraftId,
        DateTimeOffset SnapshotAt);
}
