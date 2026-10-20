using Microsoft.Data.Sqlite;
using PersonalVault.Core.Documents;

namespace PersonalVault.Core.Storage;

internal static class AuditWriter
{
    public static async Task AppendAsync(
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
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO audit_events(
                event_id, occurred_at_ms, actor_kind, client_name, session_id,
                action, document_id, result, bytes_returned, details_json)
            VALUES(
                $eventId, $occurredAt, $actor, NULL, $sessionId,
                $action, $documentId, $result, $bytesReturned, $details);
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$actor", ToActorValue(actor));
        command.Parameters.AddWithValue("$sessionId", sessionId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$documentId", documentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$bytesReturned", bytesReturned ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$details", detailsJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
