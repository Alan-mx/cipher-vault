using System.Text.Json;
using System.Globalization;
using System.Text;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Audit;

public static class AuditStore
{
    public static async Task<Guid> AppendAsync(
        VaultOperationContext context,
        VaultActorContext actor,
        string action,
        Guid? documentId,
        int? revision,
        string result,
        object? details,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid();
        var detailsJson = details is null
            ? "{}"
            : JsonSerializer.Serialize(details);
        if (detailsJson.Length > 4096)
        {
            throw new ArgumentException("Audit details exceed the bounded size.");
        }

        using var lease = context.CreateCommand();
        lease.Command.CommandText =
            """
            INSERT INTO audit_events(
                event_id, occurred_at, actor_kind, action,
                target_object_type, target_id, target_revision,
                correlation_id, result, details_json)
            VALUES(
                $eventId, $occurredAt, $actor, $action,
                'knowledge', $targetId, $revision, $correlationId, $result, $details);
            """;
        lease.Command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        lease.Command.Parameters.AddWithValue(
            "$occurredAt",
            occurredAt.ToUniversalTime().ToString("O"));
        lease.Command.Parameters.AddWithValue("$actor", ToDatabaseValue(actor.Kind));
        lease.Command.Parameters.AddWithValue("$action", action);
        lease.Command.Parameters.AddWithValue(
                "$targetId",
            documentId?.ToString("D") ?? (object)DBNull.Value);
        lease.Command.Parameters.AddWithValue(
            "$revision",
            revision ?? (object)DBNull.Value);
        lease.Command.Parameters.AddWithValue(
            "$correlationId",
            actor.CorrelationId ?? (object)DBNull.Value);
        lease.Command.Parameters.AddWithValue("$result", result);
        lease.Command.Parameters.AddWithValue("$details", detailsJson);
        await lease.Command.ExecuteNonQueryAsync(cancellationToken);
        return eventId;
    }

    public static async Task<AuditPage> QueryAsync(
        VaultOperationContext context,
        AuditQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Actor is not null && !Enum.IsDefined(query.Actor.Value))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Audit actor filter is invalid.");
        }

        if (query.OccurredAfter is not null
            && query.OccurredBefore is not null
            && query.OccurredAfter > query.OccurredBefore)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Audit time range is invalid.");
        }

        var pageSize = NormalizePageSize(query.Page?.PageSize);
        var action = string.IsNullOrWhiteSpace(query.Action)
            ? null
            : query.Action.Trim();
        var fingerprint = CursorCodec.Fingerprint(
            "audit",
            query.Actor?.ToString(),
            action,
            query.TargetObjectType,
            query.TargetId?.ToString("D"),
            query.OccurredAfter?.ToUniversalTime().ToString("O"),
            query.OccurredBefore?.ToUniversalTime().ToString("O"));
        var hasCursor = CursorCodec.TryDecode(
            query.Page?.Cursor,
            "audit",
            fingerprint,
            out AuditCursorPayload? cursor);
        if (query.Page?.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Cursor is invalid for this audit query.");
        }

        var snapshotAt = cursor?.SnapshotAt ?? now.ToUniversalTime();
        var rows = new List<(AuditItem Item, string EventId)>();
        using var read = context.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT event_id, occurred_at, actor_kind, action,
                   target_object_type, target_id, target_revision,
                   result, details_json
            FROM audit_events
            WHERE occurred_at <= $snapshotAt
            """);
        if (query.Actor is not null)
        {
            sql.Append(" AND actor_kind = $actor");
            read.Command.Parameters.AddWithValue(
                "$actor",
                ToDatabaseValue(query.Actor.Value));
        }
        if (action is not null)
        {
            sql.Append(" AND action = $action");
            read.Command.Parameters.AddWithValue("$action", action);
        }
        if (query.TargetObjectType is not null)
        {
            sql.Append(" AND target_object_type = $targetObjectType");
            read.Command.Parameters.AddWithValue(
                "$targetObjectType",
                query.TargetObjectType);
        }
        if (query.TargetId is not null)
        {
            sql.Append(" AND target_id = $targetId");
            read.Command.Parameters.AddWithValue(
                "$targetId",
                query.TargetId.Value.ToString("D"));
        }
        if (query.OccurredAfter is { } occurredAfter)
        {
            sql.Append(" AND occurred_at >= $occurredAfter");
            read.Command.Parameters.AddWithValue(
                "$occurredAfter",
                occurredAfter.ToUniversalTime().ToString("O"));
        }
        if (query.OccurredBefore is { } occurredBefore)
        {
            sql.Append(" AND occurred_at <= $occurredBefore");
            read.Command.Parameters.AddWithValue(
                "$occurredBefore",
                occurredBefore.ToUniversalTime().ToString("O"));
        }
        if (cursor is not null)
        {
            sql.Append(
                """
                 AND (
                    occurred_at < $afterOccurredAt
                    OR (
                        occurred_at = $afterOccurredAt
                        AND event_id < $afterEventId
                    )
                 )
                """);
            read.Command.Parameters.AddWithValue(
                "$afterOccurredAt",
                cursor.OccurredAt.ToUniversalTime().ToString("O"));
            read.Command.Parameters.AddWithValue(
                "$afterEventId",
                cursor.EventId.ToString("D"));
        }

        sql.Append(
            """
             ORDER BY occurred_at DESC, event_id DESC
             LIMIT $limit;
            """);
        read.Command.CommandText = sql.ToString();
        read.Command.Parameters.AddWithValue("$snapshotAt", snapshotAt.ToString("O"));
        read.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var eventId = Guid.Parse(reader.GetString(0));
            rows.Add((
                new AuditItem(
                    eventId,
                    ParseTimestamp(reader.GetString(1)),
                    ParseActor(reader.GetString(2)),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.GetString(7),
                    reader.GetString(8)),
                eventId.ToString("D")));
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                "audit",
                fingerprint,
                new AuditCursorPayload(
                    rows[^1].Item.OccurredAt,
                    Guid.Parse(rows[^1].EventId),
                    snapshotAt))
            : null;
        return new AuditPage(
            rows.Select(row => row.Item).ToArray(),
            nextCursor);
    }

    internal static string ToDatabaseValue(VaultActorKind kind) =>
        kind switch
        {
            VaultActorKind.UserUi => "user_ui",
            VaultActorKind.Mcp => "mcp",
            VaultActorKind.System => "system",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static VaultActorKind ParseActor(string value) =>
        value switch
        {
            "user_ui" => VaultActorKind.UserUi,
            "mcp" => VaultActorKind.Mcp,
            "system" => VaultActorKind.System,
            _ => throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Audit actor value is invalid.")
        };

    private static int NormalizePageSize(int? value)
    {
        var pageSize = value ?? 50;
        if (pageSize is < 1 or > 200)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Audit page size must be between 1 and 200.");
        }
        return pageSize;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record AuditCursorPayload(
        DateTimeOffset OccurredAt,
        Guid EventId,
        DateTimeOffset SnapshotAt);
}
