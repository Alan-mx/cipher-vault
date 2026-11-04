using System.Globalization;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Storage.Drafts;

public static class IdempotencyStore
{
    public static async Task<IdempotencyRecord?> FindAsync(
        VaultOperationContext context,
        Guid idempotencyKey,
        IdempotencyOperation operation,
        VaultActorKind actorKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                idempotency_key,
                actor_kind,
                operation,
                session_id,
                request_hash,
                result_json,
                created_at,
                expires_at
            FROM idempotency_records
            WHERE actor_kind = $actorKind
              AND operation = $operation
              AND idempotency_key = $idempotencyKey;
            """;
        command.Command.Parameters.AddWithValue(
            "$actorKind",
            ToDatabaseValue(actorKind));
        command.Command.Parameters.AddWithValue(
            "$operation",
            operation.ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$idempotencyKey",
            idempotencyKey.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new IdempotencyRecord(
            Guid.Parse(reader.GetString(0)),
            ParseActor(reader.GetString(1)),
            IdempotencyOperationMapping.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            ParseTimestamp(reader.GetString(6)),
            ParseTimestamp(reader.GetString(7)));
    }

    public static async Task InsertAsync(
        VaultOperationContext context,
        IdempotencyRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO idempotency_records(
                idempotency_key,
                actor_kind,
                operation,
                session_id,
                request_hash,
                result_json,
                created_at,
                expires_at)
            VALUES(
                $idempotencyKey,
                $actorKind,
                $operation,
                $sessionId,
                $requestHash,
                $resultJson,
                $createdAt,
                $expiresAt);
            """;
        command.Command.Parameters.AddWithValue(
            "$idempotencyKey",
            record.IdempotencyKey.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$actorKind",
            ToDatabaseValue(record.ActorKind));
        command.Command.Parameters.AddWithValue(
            "$operation",
            record.Operation.ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$sessionId",
            record.SessionId?.ToString("D") ?? (object)DBNull.Value);
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

    public static async Task<int> DeleteExpiredAsync(
        VaultOperationContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            DELETE FROM idempotency_records
            WHERE expires_at <= $now;
            """;
        command.Command.Parameters.AddWithValue(
            "$now",
            now.ToUniversalTime().ToString("O"));
        return await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static VaultException KeyReused() =>
        new(
            VaultErrorCode.IdempotencyKeyReused,
            "The idempotency key was already used for a different request.");

    private static string ToDatabaseValue(VaultActorKind kind) =>
        kind switch
        {
            VaultActorKind.UserUi => "user_ui",
            VaultActorKind.Mcp => "mcp",
            _ => throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Idempotency records require a UserUi or Mcp actor.")
        };

    private static VaultActorKind ParseActor(string value) =>
        value switch
        {
            "user_ui" => VaultActorKind.UserUi,
            "mcp" => VaultActorKind.Mcp,
            _ => throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Idempotency actor value is invalid.")
        };

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
}
