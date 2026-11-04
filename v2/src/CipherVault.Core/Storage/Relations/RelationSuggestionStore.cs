using System.Globalization;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Relations;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage.Relations;

public static class RelationSuggestionStore
{
    public static async Task InsertAsync(
        VaultOperationContext context,
        RelationSuggestion suggestion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(suggestion);
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO document_relation_suggestions(
                suggestion_id,
                suggestion_revision,
                payload_version,
                relation_type,
                source_document_id,
                source_document_revision,
                target_document_id,
                target_document_revision,
                reason,
                model_name,
                agent_name,
                client_name,
                evidence_json,
                status,
                source_actor,
                relation_id,
                apply_result,
                stale_reason_code,
                created_at,
                updated_at,
                terminal_at,
                applied_at)
            VALUES(
                $suggestionId,
                $suggestionRevision,
                $payloadVersion,
                $relationType,
                $sourceDocumentId,
                $sourceDocumentRevision,
                $targetDocumentId,
                $targetDocumentRevision,
                $reason,
                $modelName,
                $agentName,
                $clientName,
                $evidenceJson,
                $status,
                $sourceActor,
                $relationId,
                $applyResult,
                $staleReasonCode,
                $createdAt,
                $updatedAt,
                $terminalAt,
                $appliedAt);
            """;
        AddParameters(command.Command, suggestion);
        try
        {
            await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode == 19
                && exception.Message.Contains(
                    "ux_relation_suggestions_pending_edge",
                    StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(
                VaultErrorCode.RelationSuggestionExists,
                "A pending relation suggestion already exists for this edge.");
        }
    }

    public static async Task<RelationSuggestion?> FindAsync(
        VaultOperationContext context,
        Guid suggestionId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            $"""
             {SelectSuggestionColumns}
             FROM document_relation_suggestions
             WHERE suggestion_id = $suggestionId;
             """;
        command.Command.Parameters.AddWithValue(
            "$suggestionId",
            suggestionId.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSuggestion(reader)
            : null;
    }

    public static async Task<RelationSuggestion> GetAsync(
        VaultOperationContext context,
        Guid suggestionId,
        CancellationToken cancellationToken) =>
        await FindAsync(context, suggestionId, cancellationToken)
        ?? throw NotFound();

    public static async Task<RelationSuggestion?> FindPendingEdgeAsync(
        VaultOperationContext context,
        RelationType relationType,
        Guid sourceDocumentId,
        Guid targetDocumentId,
        CancellationToken cancellationToken)
    {
        var canonical = RelationDirection.Canonicalize(
            relationType,
            sourceDocumentId,
            targetDocumentId);
        var normalizedSource = canonical.SourceDocumentId.ToString("D");
        var normalizedTarget = canonical.TargetDocumentId.ToString("D");
        using var command = context.CreateCommand();
        command.Command.CommandText =
            $"""
             {SelectSuggestionColumns}
             FROM document_relation_suggestions
             WHERE relation_type = $relationType
               AND status = 'pending_review'
               AND (
                   (
                       $isSymmetric = 0
                       AND source_document_id = $sourceDocumentId
                       AND target_document_id = $targetDocumentId
                   )
                   OR
                   (
                       $isSymmetric = 1
                       AND (
                           (
                               source_document_id = $sourceDocumentId
                               AND target_document_id = $targetDocumentId
                           )
                           OR
                           (
                               source_document_id = $targetDocumentId
                               AND target_document_id = $sourceDocumentId
                           )
                       )
                   )
               )
             LIMIT 1;
             """;
        command.Command.Parameters.AddWithValue(
            "$relationType",
            relationType.ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$isSymmetric",
            relationType.IsDirected() ? 0 : 1);
        command.Command.Parameters.AddWithValue(
            "$sourceDocumentId",
            normalizedSource);
        command.Command.Parameters.AddWithValue(
            "$targetDocumentId",
            normalizedTarget);
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSuggestion(reader)
            : null;
    }

    public static async Task<int> CountNonTerminalAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT count(*)
            FROM document_relation_suggestions
            WHERE status IN ('pending_review', 'stale');
            """;
        return Convert.ToInt32(
            await command.Command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public static async Task UpdateAsync(
        VaultOperationContext context,
        int expectedSuggestionRevision,
        RelationSuggestion suggestion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(suggestion);
        if (suggestion.SuggestionRevision != expectedSuggestionRevision + 1)
        {
            throw new ArgumentException(
                "The next suggestion revision must be exactly one greater.",
                nameof(suggestion));
        }

        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            UPDATE document_relation_suggestions
            SET suggestion_revision = $suggestionRevision,
                source_document_revision = $sourceDocumentRevision,
                target_document_revision = $targetDocumentRevision,
                reason = $reason,
                model_name = $modelName,
                agent_name = $agentName,
                client_name = $clientName,
                evidence_json = $evidenceJson,
                status = $status,
                relation_id = $relationId,
                apply_result = $applyResult,
                stale_reason_code = $staleReasonCode,
                updated_at = $updatedAt,
                terminal_at = $terminalAt,
                applied_at = $appliedAt
            WHERE suggestion_id = $suggestionId
              AND suggestion_revision = $expectedSuggestionRevision;
            """;
        AddParameters(command.Command, suggestion);
        command.Command.Parameters.AddWithValue(
            "$expectedSuggestionRevision",
            expectedSuggestionRevision);
        if (await command.Command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return;
        }

        var current = await GetAsync(
            context,
            suggestion.SuggestionId,
            cancellationToken);
        throw RevisionConflict(current);
    }

    public static async Task<bool> DeleteAsync(
        VaultOperationContext context,
        Guid suggestionId,
        int expectedSuggestionRevision,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            DELETE FROM document_relation_suggestions
            WHERE suggestion_id = $suggestionId
              AND suggestion_revision = $expectedSuggestionRevision
              AND status IN ('pending_review', 'stale');
            """;
        command.Command.Parameters.AddWithValue(
            "$suggestionId",
            suggestionId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$expectedSuggestionRevision",
            expectedSuggestionRevision);
        return await command.Command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public static async Task<RelationSuggestionPage> ListAsync(
        VaultOperationContext context,
        RelationSuggestionQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.UpdatedAfter is { } after
            && query.UpdatedBefore is { } before
            && after >= before)
        {
            throw InvalidInput("updatedAfter must be earlier than updatedBefore.");
        }
        var pageSize = NormalizePageSize(query.Page.PageSize);
        var fingerprint = CursorCodec.Fingerprint(
            "relation-suggestions",
            query.Status.ToDatabaseValue(),
            query.RelationType?.ToDatabaseValue(),
            query.SourceDocumentId?.ToString("D"),
            query.TargetDocumentId?.ToString("D"),
            query.UpdatedAfter?.ToUniversalTime().ToString("O"),
            query.UpdatedBefore?.ToUniversalTime().ToString("O"),
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            query.Page.Cursor,
            "relation-suggestions",
            fingerprint,
            out SuggestionCursorPayload? cursor);
        if (query.Page.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Relation suggestion cursor is invalid.");
        }

        var snapshotAt = cursor?.SnapshotAt ?? now.ToUniversalTime();
        using var command = context.CreateCommand();
        command.Command.CommandText =
            $"""
             {SelectListColumns}
             FROM document_relation_suggestions AS s
             JOIN documents AS source
               ON source.document_id = s.source_document_id
             JOIN documents AS target
               ON target.document_id = s.target_document_id
             WHERE s.status = $status
               AND s.updated_at <= $snapshotAt
               AND ($relationType IS NULL
                    OR s.relation_type = $relationType)
               AND ($sourceDocumentId IS NULL
                    OR s.source_document_id = $sourceDocumentId)
               AND ($targetDocumentId IS NULL
                    OR s.target_document_id = $targetDocumentId)
               AND ($updatedAfter IS NULL
                    OR s.updated_at >= $updatedAfter)
               AND ($updatedBefore IS NULL
                    OR s.updated_at < $updatedBefore)
               AND (
                   $afterUpdatedAt IS NULL
                   OR s.updated_at < $afterUpdatedAt
                   OR (
                       s.updated_at = $afterUpdatedAt
                       AND s.suggestion_id < $afterSuggestionId
                   )
               )
             ORDER BY s.updated_at DESC, s.suggestion_id DESC
             LIMIT $limit;
             """;
        command.Command.Parameters.AddWithValue(
            "$status",
            query.Status.ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$relationType",
            query.RelationType?.ToDatabaseValue()
            ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$sourceDocumentId",
            query.SourceDocumentId?.ToString("D")
            ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$targetDocumentId",
            query.TargetDocumentId?.ToString("D")
            ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$updatedAfter",
            query.UpdatedAfter?.ToUniversalTime().ToString("O")
            ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$updatedBefore",
            query.UpdatedBefore?.ToUniversalTime().ToString("O")
            ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$afterUpdatedAt",
            cursor?.UpdatedAt.ToUniversalTime().ToString("O")
            ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$afterSuggestionId",
            cursor?.SuggestionId.ToString("D")
            ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$snapshotAt",
            snapshotAt.ToString("O"));
        command.Command.Parameters.AddWithValue("$limit", pageSize + 1);

        var rows = new List<RelationSuggestionListItem>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadList(reader));
        }
        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                "relation-suggestions",
                fingerprint,
                new SuggestionCursorPayload(
                    rows[^1].UpdatedAt,
                    rows[^1].SuggestionId,
                    snapshotAt))
            : null;
        return new RelationSuggestionPage(rows, nextCursor);
    }

    public static async Task<RelationSuggestionSummary> GetSummaryAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                coalesce(sum(
                    CASE WHEN status = 'pending_review' THEN 1 ELSE 0 END
                ), 0),
                coalesce(sum(
                    CASE WHEN status = 'stale' THEN 1 ELSE 0 END
                ), 0),
                coalesce(sum(
                    CASE WHEN status = 'applied' THEN 1 ELSE 0 END
                ), 0),
                coalesce(sum(
                    CASE WHEN status = 'rejected' THEN 1 ELSE 0 END
                ), 0),
                coalesce(sum(
                    CASE WHEN status = 'expired' THEN 1 ELSE 0 END
                ), 0)
            FROM document_relation_suggestions;
            """;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new RelationSuggestionSummary(0, 0, 0, 0, 0);
        }
        return new RelationSuggestionSummary(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    public static async Task<IReadOnlyList<RelationSuggestionStaleUpdate>>
        MarkPendingStaleForDocumentAsync(
            VaultOperationContext context,
            Guid documentId,
            bool trashed,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            UPDATE document_relation_suggestions
            SET status = 'stale',
                stale_reason_code = CASE
                    WHEN source_document_id = $documentId
                        THEN $sourceReason
                    ELSE $targetReason
                END,
                suggestion_revision = suggestion_revision + 1,
                updated_at = $updatedAt
            WHERE status = 'pending_review'
              AND (
                  source_document_id = $documentId
                  OR target_document_id = $documentId
              )
            RETURNING
                suggestion_id,
                suggestion_revision,
                relation_type,
                source_document_id,
                target_document_id,
                stale_reason_code;
            """;
        command.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$sourceReason",
            (trashed
                ? RelationStaleReason.SourceTrashed
                : RelationStaleReason.SourceRevisionChanged)
            .ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$targetReason",
            (trashed
                ? RelationStaleReason.TargetTrashed
                : RelationStaleReason.TargetRevisionChanged)
            .ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$updatedAt",
            now.ToUniversalTime().ToString("O"));
        var result = new List<RelationSuggestionStaleUpdate>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RelationSuggestionStaleUpdate(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                RelationValueMapping.ParseRelationType(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)),
                Guid.Parse(reader.GetString(4)),
                RelationValueMapping.ParseStaleReason(reader.GetString(5))
                ?? throw Corrupt("Stale reason is missing.")));
        }
        return result;
    }

    public static async Task<int> DeleteTerminalBeforeAsync(
        VaultOperationContext context,
        DateTimeOffset terminalBefore,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            DELETE FROM document_relation_suggestions
            WHERE terminal_at IS NOT NULL
              AND terminal_at <= $terminalBefore;
            """;
        command.Command.Parameters.AddWithValue(
            "$terminalBefore",
            terminalBefore.ToUniversalTime().ToString("O"));
        return await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(
        SqliteCommand command,
        RelationSuggestion suggestion)
    {
        command.Parameters.AddWithValue(
            "$suggestionId",
            suggestion.SuggestionId.ToString("D"));
        command.Parameters.AddWithValue(
            "$suggestionRevision",
            suggestion.SuggestionRevision);
        command.Parameters.AddWithValue(
            "$payloadVersion",
            suggestion.PayloadVersion);
        command.Parameters.AddWithValue(
            "$relationType",
            suggestion.RelationType.ToDatabaseValue());
        command.Parameters.AddWithValue(
            "$sourceDocumentId",
            suggestion.SourceDocumentId.ToString("D"));
        command.Parameters.AddWithValue(
            "$sourceDocumentRevision",
            suggestion.SourceDocumentRevision);
        command.Parameters.AddWithValue(
            "$targetDocumentId",
            suggestion.TargetDocumentId.ToString("D"));
        command.Parameters.AddWithValue(
            "$targetDocumentRevision",
            suggestion.TargetDocumentRevision);
        command.Parameters.AddWithValue("$reason", suggestion.Reason);
        command.Parameters.AddWithValue(
            "$modelName",
            suggestion.ModelName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$agentName",
            suggestion.AgentName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$clientName",
            suggestion.ClientName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$evidenceJson",
            RelationEvidenceCodec.Serialize(suggestion.Evidence));
        command.Parameters.AddWithValue(
            "$status",
            suggestion.Status.ToDatabaseValue());
        command.Parameters.AddWithValue(
            "$sourceActor",
            suggestion.SourceActor);
        command.Parameters.AddWithValue(
            "$relationId",
            suggestion.RelationId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$applyResult",
            suggestion.ApplyResult.ToDatabaseValue()
            ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$staleReasonCode",
            suggestion.StaleReason.ToDatabaseValue()
            ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$createdAt",
            suggestion.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$updatedAt",
            suggestion.UpdatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$terminalAt",
            suggestion.TerminalAt?.ToUniversalTime().ToString("O")
            ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$appliedAt",
            suggestion.AppliedAt?.ToUniversalTime().ToString("O")
            ?? (object)DBNull.Value);
    }

    private static RelationSuggestion ReadSuggestion(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetInt32(2),
            RelationValueMapping.ParseRelationType(reader.GetString(3)),
            Guid.Parse(reader.GetString(4)),
            reader.GetInt32(5),
            Guid.Parse(reader.GetString(6)),
            reader.GetInt32(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            RelationEvidenceCodec.Deserialize(reader.GetString(12)),
            RelationValueMapping.ParseSuggestionStatus(reader.GetString(13)),
            reader.GetString(14),
            reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)),
            RelationValueMapping.ParseApplyResult(
                reader.IsDBNull(16) ? null : reader.GetString(16)),
            RelationValueMapping.ParseStaleReason(
                reader.IsDBNull(17) ? null : reader.GetString(17)),
            ParseTimestamp(reader.GetString(18)),
            ParseTimestamp(reader.GetString(19)),
            reader.IsDBNull(20) ? null : ParseTimestamp(reader.GetString(20)),
            reader.IsDBNull(21) ? null : ParseTimestamp(reader.GetString(21)));

    private static RelationSuggestionListItem ReadList(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            RelationValueMapping.ParseRelationType(reader.GetString(3)),
            RelationValueMapping.ParseSuggestionStatus(reader.GetString(13)),
            reader.GetInt32(5),
            reader.GetInt32(7),
            new RelationDocumentReference(
                Guid.Parse(reader.GetString(4)),
                reader.GetString(22),
                reader.GetString(23),
                reader.GetInt32(24)),
            new RelationDocumentReference(
                Guid.Parse(reader.GetString(6)),
                reader.GetString(26),
                reader.GetString(27),
                reader.GetInt32(28)),
            reader.GetString(14),
            reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)),
            RelationValueMapping.ParseApplyResult(
                reader.IsDBNull(16) ? null : reader.GetString(16)),
            RelationValueMapping.ParseStaleReason(
                reader.IsDBNull(17) ? null : reader.GetString(17)),
            ParseTimestamp(reader.GetString(18)),
            ParseTimestamp(reader.GetString(19)),
            reader.IsDBNull(20) ? null : ParseTimestamp(reader.GetString(20)));

    private static int NormalizePageSize(int? value)
    {
        var pageSize = value ?? 50;
        if (pageSize is < 1 or > 200)
        {
            throw InvalidInput(
                "Relation suggestion page size must be between 1 and 200.");
        }
        return pageSize;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static VaultException NotFound() =>
        new(
            VaultErrorCode.RelationSuggestionNotFound,
            "Relation suggestion was not found.");

    private static VaultException RevisionConflict(RelationSuggestion current) =>
        new(
            VaultErrorCode.RelationSuggestionRevisionConflict,
            "Relation suggestion revision changed.",
            details: new Dictionary<string, object?>
            {
                ["currentSuggestionRevision"] =
                    current.SuggestionRevision,
                ["status"] = current.Status.ToDatabaseValue()
            });

    private static VaultException InvalidInput(string message) =>
        new(VaultErrorCode.InvalidInput, message);

    private static VaultException Corrupt(string message) =>
        new(VaultErrorCode.CorruptVault, message);

    private const string SelectSuggestionColumns =
        """
        SELECT
            suggestion_id,
            suggestion_revision,
            payload_version,
            relation_type,
            source_document_id,
            source_document_revision,
            target_document_id,
            target_document_revision,
            reason,
            model_name,
            agent_name,
            client_name,
            evidence_json,
            status,
            source_actor,
            relation_id,
            apply_result,
            stale_reason_code,
            created_at,
            updated_at,
            terminal_at,
            applied_at
        """;

    private const string SelectListColumns =
        """
        SELECT
            s.suggestion_id,
            s.suggestion_revision,
            s.payload_version,
            s.relation_type,
            s.source_document_id,
            s.source_document_revision,
            s.target_document_id,
            s.target_document_revision,
            s.reason,
            s.model_name,
            s.agent_name,
            s.client_name,
            s.evidence_json,
            s.status,
            s.source_actor,
            s.relation_id,
            s.apply_result,
            s.stale_reason_code,
            s.created_at,
            s.updated_at,
            s.terminal_at,
            s.applied_at,
            source.title,
            source.path,
            source.revision,
            source.deleted_at,
            target.title,
            target.path,
            target.revision,
            target.deleted_at
        """;

    private sealed record SuggestionCursorPayload(
        DateTimeOffset UpdatedAt,
        Guid SuggestionId,
        DateTimeOffset SnapshotAt);
}
