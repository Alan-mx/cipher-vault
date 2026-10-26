using Microsoft.Data.Sqlite;
using CipherVault.Core.Documents;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CipherVault.Core.Storage;

internal static class AuditWriter
{
    public static Task<Guid> AppendAsync(
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

    public static async Task<Guid> AppendAsync(
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
        var eventId = await AppendAsync(
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
        return eventId;
    }

    public static async Task<bool> TryAppendAsync(
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
        try
        {
            await AppendAsync(
                connection,
                actor,
                sessionId,
                action,
                documentId,
                result,
                bytesReturned,
                detailsJson,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            Trace.TraceError("CipherVault audit event write was cancelled after publication.");
            return false;
        }
        catch (Exception exception) when (
            exception is SqliteException or InvalidOperationException)
        {
            Trace.TraceError($"CipherVault audit event write failed: {exception.GetType().Name}");
            return false;
        }
    }

    public static Task<Guid> AppendAsync(
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

    public static async Task<Guid> AppendAsync(
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
                action, document_id, correlation_id, result, bytes_returned,
                details_json)
            VALUES(
                $eventId, $occurredAt, $actor, $clientName, $sessionId,
                $action, $documentId, $correlationId, $result, $bytesReturned,
                $details);
            """;
        var eventId = Guid.NewGuid();
        command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
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
        command.Parameters.AddWithValue(
            "$correlationId",
            ExtractCorrelationId(detailsJson) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$bytesReturned", bytesReturned ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$details",
            MergeDetails(detailsJson, auditContext));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return eventId;
    }

    private static string? ExtractCorrelationId(string detailsJson)
    {
        try
        {
            return JsonNode.Parse(detailsJson)?
                ["correlationId"]?
                .GetValue<string>();
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string MergeDetails(
        string detailsJson,
        VaultAuditContext auditContext)
    {
        if (auditContext.RequestId is null &&
            auditContext.ClientInstanceId is null &&
            auditContext.GrantId is null &&
            auditContext.GrantScopes is null)
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
        if (auditContext.RequestId is not null)
        {
            details["requestId"] = auditContext.RequestId;
        }
        if (auditContext.ClientInstanceId is not null)
        {
            details["clientInstanceId"] = auditContext.ClientInstanceId.ToString()!;
        }
        if (auditContext.GrantId is not null)
        {
            details["grantId"] = auditContext.GrantId.ToString()!;
        }
        if (auditContext.GrantScopes is not null)
        {
            details["grantScopes"] = auditContext.GrantScopes;
        }
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
