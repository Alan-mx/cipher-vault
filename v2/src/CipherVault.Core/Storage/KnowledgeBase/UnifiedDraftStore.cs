using System.Globalization;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Core.Storage.KnowledgeBase;

public static class UnifiedDraftStore
{
    public static async Task InsertAsync(
        VaultOperationContext context,
        DraftRecord record,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO drafts(
                draft_id, draft_revision, target_object_type, operation_type,
                target_id, base_revision, base_model_version, payload_version,
                payload_json, base_snapshot_json, status, source_actor,
                created_at, updated_at, terminal_at, applied_object_type,
                applied_object_id, applied_object_revision, applied_at)
            VALUES(
                $draftId, $draftRevision, $targetObjectType, $operationType,
                $targetId, $baseRevision, $baseModelVersion, $payloadVersion,
                $payload, $baseSnapshot, $status, $sourceActor,
                $createdAt, $updatedAt, $terminalAt, $appliedObjectType,
                $appliedObjectId, $appliedObjectRevision, $appliedAt);
            """;
        AddParameters(command.Command, record);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<DraftRecord> GetAsync(
        VaultOperationContext context,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                draft_id, draft_revision, target_object_type, operation_type,
                target_id, base_revision, base_model_version, payload_version,
                payload_json, base_snapshot_json, status, source_actor,
                created_at, updated_at, terminal_at, applied_object_type,
                applied_object_id, applied_object_revision, applied_at
            FROM drafts
            WHERE draft_id = $draftId;
            """;
        command.Command.Parameters.AddWithValue(
            "$draftId",
            draftId.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                VaultErrorCode.DraftNotFound,
                "Draft was not found.");
        }
        return ReadRecord(reader);
    }

    public static async Task<DraftSummary> GetSummaryAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                coalesce(sum(CASE WHEN status IN ('draft','conflict')
                    THEN 1 ELSE 0 END), 0),
                coalesce(sum(CASE WHEN status = 'pending_review'
                    THEN 1 ELSE 0 END), 0),
                coalesce(sum(CASE WHEN status IN (
                    'applied','rejected','discarded','expired')
                    THEN 1 ELSE 0 END), 0),
                coalesce(sum(CASE WHEN status = 'draft'
                    AND target_object_type = 'knowledge'
                    THEN 1 ELSE 0 END), 0)
            FROM drafts;
            """;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new DraftSummary(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    public static async Task<PagedResult<DraftListItem>> ListAsync(
        VaultOperationContext context,
        DraftQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = query.PageSize is < 1 or > 200
            ? throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Draft pageSize must be between 1 and 200.")
            : query.PageSize;
        var fingerprint = CursorCodec.Fingerprint(
            "drafts",
            query.Status?.ToDatabaseValue(),
            query.TargetObjectType?.ToDatabaseValue(),
            query.OperationType?.ToDatabaseValue(),
            query.SourceActor,
            query.TargetId?.ToString("D"),
            query.UpdatedAfter?.ToUniversalTime().ToString("O"),
            query.UpdatedBefore?.ToUniversalTime().ToString("O"),
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            query.Cursor,
            "drafts",
            fingerprint,
            out DraftCursor? cursor);
        if (query.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Draft cursor is invalid for this query.");
        }

        using var command = context.CreateCommand();
        var sql = new System.Text.StringBuilder(
            """
            SELECT
                draft_id, draft_revision, target_object_type, operation_type,
                target_id, base_revision, payload_json, status, source_actor,
                created_at, updated_at
            FROM drafts
            WHERE 1 = 1
            """);
        if (query.Status is { } status)
        {
            sql.Append(" AND status = $status");
            command.Command.Parameters.AddWithValue(
                "$status",
                status.ToDatabaseValue());
        }
        if (query.TargetObjectType is { } targetType)
        {
            sql.Append(" AND target_object_type = $targetType");
            command.Command.Parameters.AddWithValue(
                "$targetType",
                targetType.ToDatabaseValue());
        }
        if (query.OperationType is { } operation)
        {
            sql.Append(" AND operation_type = $operation");
            command.Command.Parameters.AddWithValue(
                "$operation",
                operation.ToDatabaseValue());
        }
        if (query.SourceActor is { } actor)
        {
            sql.Append(" AND source_actor = $sourceActor");
            command.Command.Parameters.AddWithValue("$sourceActor", actor);
        }
        if (query.TargetId is { } targetId)
        {
            sql.Append(" AND target_id = $targetId");
            command.Command.Parameters.AddWithValue(
                "$targetId",
                targetId.ToString("D"));
        }
        if (query.UpdatedAfter is { } after)
        {
            sql.Append(" AND updated_at >= $updatedAfter");
            command.Command.Parameters.AddWithValue(
                "$updatedAfter",
                after.ToUniversalTime().ToString("O"));
        }
        if (query.UpdatedBefore is { } before)
        {
            sql.Append(" AND updated_at < $updatedBefore");
            command.Command.Parameters.AddWithValue(
                "$updatedBefore",
                before.ToUniversalTime().ToString("O"));
        }
        if (cursor is not null)
        {
            sql.Append(
                """
                 AND (
                    updated_at < $afterUpdatedAt
                    OR (updated_at = $afterUpdatedAt AND draft_id < $afterId)
                 )
                """);
            command.Command.Parameters.AddWithValue(
                "$afterUpdatedAt",
                cursor.UpdatedAt);
            command.Command.Parameters.AddWithValue("$afterId", cursor.Id);
        }
        sql.Append(
            " ORDER BY updated_at DESC, draft_id DESC LIMIT $limit;");
        command.Command.CommandText = sql.ToString();
        command.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        var items = new List<DraftListItem>();
        string lastUpdated = string.Empty;
        string lastId = string.Empty;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lastUpdated = reader.GetString(10);
            lastId = reader.GetString(0);
            var payload = ParseJson(reader.GetString(6));
            items.Add(new DraftListItem(
                Guid.Parse(lastId),
                reader.GetInt32(1),
                KnowledgeBaseValueMapping.ParseTargetObjectType(
                    reader.GetString(2)),
                KnowledgeBaseValueMapping.ParseOperationType(
                    reader.GetString(3)),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                KnowledgeBaseValueMapping.ParseDraftStatus(reader.GetString(7)),
                reader.GetString(8),
                ReadTitle(payload),
                ParseTimestamp(reader.GetString(9)),
                ParseTimestamp(lastUpdated)));
        }
        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        return new PagedResult<DraftListItem>(
            items,
            hasMore
                ? CursorCodec.Encode(
                    "drafts",
                    fingerprint,
                    new DraftCursor(lastUpdated, lastId))
                : null,
            hasMore,
            null);
    }

    public static async Task<DraftRecord> UpdateAsync(
        VaultOperationContext context,
        Guid draftId,
        int expectedRevision,
        JsonElement payload,
        DraftStatus status,
        JsonElement? baseSnapshot,
        int? baseRevision,
        int? baseModelVersion,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        int affected;
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                UPDATE drafts
                SET
                    draft_revision = draft_revision + 1,
                    payload_json = $payload,
                    status = $status,
                    base_snapshot_json = coalesce($baseSnapshot, base_snapshot_json),
                    base_revision = coalesce($baseRevision, base_revision),
                    base_model_version =
                        coalesce($baseModelVersion, base_model_version),
                    updated_at = $updatedAt
                WHERE draft_id = $draftId
                  AND draft_revision = $expectedRevision
                  AND status IN ('draft', 'conflict', 'pending_review');
                """;
            command.Command.Parameters.AddWithValue(
                "$payload",
                payload.GetRawText());
            command.Command.Parameters.AddWithValue(
                "$status",
                status.ToDatabaseValue());
            command.Command.Parameters.AddWithValue(
                "$baseSnapshot",
                baseSnapshot is { } snapshot
                    ? snapshot.GetRawText()
                    : DBNull.Value);
            command.Command.Parameters.AddWithValue(
                "$baseRevision",
                baseRevision ?? (object)DBNull.Value);
            command.Command.Parameters.AddWithValue(
                "$baseModelVersion",
                baseModelVersion ?? (object)DBNull.Value);
            command.Command.Parameters.AddWithValue(
                "$updatedAt",
                updatedAt.ToUniversalTime().ToString("O"));
            command.Command.Parameters.AddWithValue(
                "$draftId",
                draftId.ToString("D"));
            command.Command.Parameters.AddWithValue(
                "$expectedRevision",
                expectedRevision);
            affected =
                await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (affected != 1)
        {
            var current = await GetAsync(context, draftId, cancellationToken);
            if (current.DraftRevision != expectedRevision)
            {
                throw new VaultException(
                    VaultErrorCode.DraftRevisionConflict,
                    "Draft revision changed.",
                    details: new Dictionary<string, object?>
                    {
                        ["currentDraftRevision"] = current.DraftRevision
                    });
            }
            throw new VaultException(
                VaultErrorCode.DraftStateConflict,
                "Draft state does not allow this operation.");
        }
        return await GetAsync(context, draftId, cancellationToken);
    }

    public static async Task<DraftRecord> SetStatusAsync(
        VaultOperationContext context,
        Guid draftId,
        int expectedRevision,
        DraftStatus targetStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (targetStatus is not (
                DraftStatus.PendingReview
                or DraftStatus.Conflict
                or DraftStatus.Discarded
                or DraftStatus.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(targetStatus));
        }
        int affected;
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                UPDATE drafts
                SET
                    draft_revision = draft_revision + 1,
                    status = $status,
                    updated_at = $updatedAt,
                    terminal_at = CASE
                        WHEN $status IN ('discarded','rejected') THEN $updatedAt
                        ELSE NULL
                    END
                WHERE draft_id = $draftId
                  AND draft_revision = $expectedRevision
                  AND (
                    ($status = 'pending_review' AND status = 'draft')
                    OR ($status = 'discarded'
                        AND status IN ('draft','pending_review','conflict'))
                    OR ($status = 'rejected' AND status = 'pending_review')
                    OR ($status = 'conflict'
                        AND status IN ('draft','pending_review'))
                  );
                """;
            command.Command.Parameters.AddWithValue(
                "$status",
                targetStatus.ToDatabaseValue());
            command.Command.Parameters.AddWithValue(
                "$updatedAt",
                now.ToUniversalTime().ToString("O"));
            command.Command.Parameters.AddWithValue(
                "$draftId",
                draftId.ToString("D"));
            command.Command.Parameters.AddWithValue(
                "$expectedRevision",
                expectedRevision);
            affected =
                await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (affected != 1)
        {
            var current = await GetAsync(context, draftId, cancellationToken);
            if (current.DraftRevision != expectedRevision)
            {
                throw new VaultException(
                    VaultErrorCode.DraftRevisionConflict,
                    "Draft revision changed.");
            }
            throw new VaultException(
                VaultErrorCode.DraftStateConflict,
                "Draft state does not allow this operation.");
        }
        return await GetAsync(context, draftId, cancellationToken);
    }

    public static async Task<DraftRecord> MarkAppliedAsync(
        VaultOperationContext context,
        Guid draftId,
        int expectedRevision,
        DraftTargetObjectType objectType,
        Guid objectId,
        int objectRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int affected;
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                UPDATE drafts
                SET
                    draft_revision = draft_revision + 1,
                    status = 'applied',
                    updated_at = $updatedAt,
                    terminal_at = $updatedAt,
                    applied_object_type = $objectType,
                    applied_object_id = $objectId,
                    applied_object_revision = $objectRevision,
                    applied_at = $updatedAt
                WHERE draft_id = $draftId
                  AND draft_revision = $expectedRevision
                  AND status = 'pending_review'
                  AND target_object_type = $objectType;
                """;
            command.Command.Parameters.AddWithValue(
                "$updatedAt",
                now.ToUniversalTime().ToString("O"));
            command.Command.Parameters.AddWithValue(
                "$objectType",
                objectType.ToDatabaseValue());
            command.Command.Parameters.AddWithValue(
                "$objectId",
                objectId.ToString("D"));
            command.Command.Parameters.AddWithValue(
                "$objectRevision",
                objectRevision);
            command.Command.Parameters.AddWithValue(
                "$draftId",
                draftId.ToString("D"));
            command.Command.Parameters.AddWithValue(
                "$expectedRevision",
                expectedRevision);
            affected =
                await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (affected != 1)
        {
            throw new VaultException(
                VaultErrorCode.DraftStateConflict,
                "Draft cannot be applied.");
        }
        return await GetAsync(context, draftId, cancellationToken);
    }

    private static void AddParameters(
        Microsoft.Data.Sqlite.SqliteCommand command,
        DraftRecord record)
    {
        command.Parameters.AddWithValue(
            "$draftId",
            record.DraftId.ToString("D"));
        command.Parameters.AddWithValue(
            "$draftRevision",
            record.DraftRevision);
        command.Parameters.AddWithValue(
            "$targetObjectType",
            record.TargetObjectType.ToDatabaseValue());
        command.Parameters.AddWithValue(
            "$operationType",
            record.OperationType.ToDatabaseValue());
        command.Parameters.AddWithValue(
            "$targetId",
            record.TargetId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseRevision",
            record.BaseRevision ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseModelVersion",
            record.BaseModelVersion);
        command.Parameters.AddWithValue(
            "$payloadVersion",
            record.PayloadVersion);
        command.Parameters.AddWithValue(
            "$payload",
            record.Payload.GetRawText());
        command.Parameters.AddWithValue(
            "$baseSnapshot",
            record.BaseSnapshot is { } snapshot
                ? snapshot.GetRawText()
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$status",
            record.Status.ToDatabaseValue());
        command.Parameters.AddWithValue("$sourceActor", record.SourceActor);
        command.Parameters.AddWithValue(
            "$createdAt",
            record.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$updatedAt",
            record.UpdatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$terminalAt",
            record.TerminalAt?.ToUniversalTime().ToString("O")
            ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$appliedObjectType",
            record.AppliedObjectType?.ToDatabaseValue()
            ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$appliedObjectId",
            record.AppliedObjectId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$appliedObjectRevision",
            record.AppliedObjectRevision ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$appliedAt",
            record.AppliedAt?.ToUniversalTime().ToString("O")
            ?? (object)DBNull.Value);
    }

    private static DraftRecord ReadRecord(
        Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            KnowledgeBaseValueMapping.ParseTargetObjectType(reader.GetString(2)),
            KnowledgeBaseValueMapping.ParseOperationType(reader.GetString(3)),
            reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            ParseJson(reader.GetString(8)),
            reader.IsDBNull(9) ? null : ParseJson(reader.GetString(9)),
            KnowledgeBaseValueMapping.ParseDraftStatus(reader.GetString(10)),
            reader.GetString(11),
            ParseTimestamp(reader.GetString(12)),
            ParseTimestamp(reader.GetString(13)),
            reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)),
            reader.IsDBNull(15)
                ? null
                : KnowledgeBaseValueMapping.ParseTargetObjectType(
                    reader.GetString(15)),
            reader.IsDBNull(16) ? null : Guid.Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : reader.GetInt32(17),
            reader.IsDBNull(18) ? null : ParseTimestamp(reader.GetString(18)));

    private static string? ReadTitle(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return payload.TryGetProperty("title", out var title)
            && title.ValueKind == JsonValueKind.String
            ? title.GetString()
            : null;
    }

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record DraftCursor(string UpdatedAt, string Id);
}
