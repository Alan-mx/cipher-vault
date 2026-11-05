using System.Globalization;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Storage.KnowledgeBase;

public sealed record KnowledgeBaseIdempotencyRecord(
    Guid Key,
    string ActorKind,
    string Operation,
    string RequestHash,
    string ResultJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public static class KnowledgeBaseIdempotencyStore
{
    public static async Task<KnowledgeBaseIdempotencyRecord?> FindAsync(
        VaultOperationContext context,
        Guid key,
        string operation,
        VaultActorKind actor,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                idempotency_key,
                actor_kind,
                operation,
                request_hash,
                result_json,
                created_at,
                expires_at
            FROM idempotency_records
            WHERE idempotency_key = $key;
            """;
        command.Command.Parameters.AddWithValue("$key", key.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        if (!string.Equals(
                reader.GetString(1),
                ActorValue(actor),
                StringComparison.Ordinal)
            || !string.Equals(
                reader.GetString(2),
                operation,
                StringComparison.Ordinal))
        {
            throw KeyReused();
        }
        return new KnowledgeBaseIdempotencyRecord(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)));
    }

    public static async Task InsertAsync(
        VaultOperationContext context,
        KnowledgeBaseIdempotencyRecord record,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO idempotency_records(
                idempotency_key,
                actor_kind,
                operation,
                request_hash,
                result_json,
                created_at,
                expires_at)
            VALUES(
                $key,
                $actorKind,
                $operation,
                $requestHash,
                $resultJson,
                $createdAt,
                $expiresAt);
            """;
        command.Command.Parameters.AddWithValue(
            "$key",
            record.Key.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$actorKind",
            record.ActorKind);
        command.Command.Parameters.AddWithValue(
            "$operation",
            record.Operation);
        command.Command.Parameters.AddWithValue(
            "$requestHash",
            record.RequestHash);
        command.Command.Parameters.AddWithValue(
            "$resultJson",
            record.ResultJson);
        command.Command.Parameters.AddWithValue(
            "$createdAt",
            record.CreatedAt.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue(
            "$expiresAt",
            record.ExpiresAt.ToUniversalTime().ToString("O"));
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static VaultException KeyReused() =>
        new(
            VaultErrorCode.IdempotencyKeyReused,
            "The idempotency key was used for a different request.");

    private static string ActorValue(VaultActorKind actor) =>
        actor switch
        {
            VaultActorKind.UserUi => "user_ui",
            VaultActorKind.Mcp => "mcp",
            _ => throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Idempotency requires a user_ui or mcp actor.")
        };

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
}
