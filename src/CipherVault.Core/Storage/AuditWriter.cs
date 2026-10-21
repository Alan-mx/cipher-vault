using Microsoft.Data.Sqlite;
using CipherVault.Core.Documents;
using System.Text.Json.Nodes;

namespace CipherVault.Core.Storage;

internal static class AuditWriter
{
    public static Task AppendAsync(
        SqliteConnection connection,
        VaultActorKind actor,
        Guid? sessionId,
        string action,
        Guid? documentId,
        string result,
        int? bytesReturned,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        return AppendAsync(
            connection,
            new VaultAuditContext(actor),
            sessionId,
            action,
            documentId,
            result,
            bytesReturned,
            detailsJson,
            cancellationToken);
    }

    public static async Task AppendAsync(
        SqliteConnection connection,
        VaultAuditContext auditContext,
        Guid? sessionId,
        string action,
        Guid? documentId,
        string result,
        int? bytesReturned,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await AppendAsync(
            connection,
            (SqliteTransaction)transaction,
            auditContext,
            sessionId,
            action,
            documentId,
            result,
            bytesReturned,
            detailsJson,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Task AppendAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VaultActorKind actor,
        Guid? sessionId,
        string action,
        Guid? documentId,
        string result,
        int? bytesReturned,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        return AppendAsync(
            connection,
            transaction,
            new VaultAuditContext(actor),
            sessionId,
            action,
            documentId,
            result,
            bytesReturned,
            detailsJson,
            cancellationToken);
    }

    public static async Task AppendAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VaultAuditContext auditContext,
        Guid? sessionId,
        string action,
        Guid? documentId,
        string result,
        int? bytesReturned,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO audit_events(
                event_id, occurred_at_ms, actor_kind, client_name, session_id,
                action, document_id, result, bytes_returned, details_json)
            VALUES(
                $eventId, $occurredAt, $actor, $clientName, $sessionId,
                $action, $documentId, $result, $bytesReturned, $details);
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$actor", ToActorValue(auditContext.Actor));
        command.Parameters.AddWithValue(
            "$clientName",
            string.IsNullOrWhiteSpace(auditContext.ClientName)
                ? DBNull.Value
                : auditContext.ClientName);
        command.Parameters.AddWithValue("$sessionId", sessionId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$documentId", documentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$bytesReturned", bytesReturned ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$details",
            AddRequestId(detailsJson, auditContext.RequestId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string AddRequestId(string detailsJson, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return detailsJson;
        }

        JsonObject details;
        try
        {
            details = JsonNode.Parse(detailsJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            details = new JsonObject();
        }
        details["requestId"] = requestId;
        return details.ToJsonString();
    }

    public static string ToActorValue(VaultActorKind actor)
    {
        return actor switch
        {
            VaultActorKind.UserUi => "user_ui",
            VaultActorKind.UserCli => "user_cli",
            VaultActorKind.Mcp => "mcp",
            VaultActorKind.System => "system",
            _ => "system"
        };
    }
}
