using System.Globalization;
using CipherVault.Core.Database;
using CipherVault.Core.Semantic;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage.Semantic;

public static class SemanticProfileStore
{
    public static async Task<SemanticProfile?> GetActiveAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            $"""
             {SelectColumns}
             FROM semantic_profiles
             WHERE is_active = 1
             LIMIT 1;
             """;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadProfile(reader)
            : null;
    }

    public static async Task<SemanticProfile?> FindAsync(
        VaultOperationContext context,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            $"""
             {SelectColumns}
             FROM semantic_profiles
             WHERE profile_id = $profileId
             LIMIT 1;
             """;
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadProfile(reader)
            : null;
    }

    public static async Task ActivateAsync(
        VaultOperationContext context,
        SemanticProfile profile,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profile);
        using (var demote = context.CreateCommand())
        {
            demote.Command.CommandText =
                """
                UPDATE semantic_profiles
                SET is_active = 0,
                    status = 'invalid',
                    updated_at = $updatedAt
                WHERE is_active = 1
                  AND profile_id <> $profileId;
                """;
            demote.Command.Parameters.AddWithValue(
                "$updatedAt",
                now.ToUniversalTime().ToString("O"));
            demote.Command.Parameters.AddWithValue(
                "$profileId",
                profile.ProfileId);
            await demote.Command.ExecuteNonQueryAsync(cancellationToken);
        }

        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO semantic_profiles(
                profile_id,
                provider_id,
                model_id,
                dimension,
                max_input_tokens,
                profile_version,
                chunker_version,
                endpoint_fingerprint,
                is_remote,
                is_active,
                status,
                last_error_code,
                created_at,
                updated_at,
                ready_at)
            VALUES(
                $profileId,
                $providerId,
                $modelId,
                $dimension,
                $maxInputTokens,
                $profileVersion,
                $chunkerVersion,
                $endpointFingerprint,
                $isRemote,
                1,
                $status,
                $lastErrorCode,
                $createdAt,
                $updatedAt,
                $readyAt)
            ON CONFLICT(profile_id) DO UPDATE SET
                is_active = 1,
                status = excluded.status,
                last_error_code = excluded.last_error_code,
                updated_at = excluded.updated_at,
                ready_at = excluded.ready_at;
            """;
        AddProfileParameters(command.Command, profile);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task UpdateStatusAsync(
        VaultOperationContext context,
        string profileId,
        SemanticProfileStatus status,
        string? lastErrorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            UPDATE semantic_profiles
            SET status = $status,
                last_error_code = $lastErrorCode,
                updated_at = $updatedAt,
                ready_at = CASE
                    WHEN $status = 'ready' THEN coalesce(ready_at, $updatedAt)
                    ELSE ready_at
                END
            WHERE profile_id = $profileId;
            """;
        command.Command.Parameters.AddWithValue(
            "$status",
            status.ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$lastErrorCode",
            lastErrorCode ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$updatedAt",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SelectColumns =
        """
        SELECT
            profile_id,
            provider_id,
            model_id,
            dimension,
            max_input_tokens,
            profile_version,
            chunker_version,
            endpoint_fingerprint,
            is_remote,
            is_active,
            status,
            last_error_code,
            created_at,
            updated_at,
            ready_at
        """;

    private static void AddProfileParameters(
        SqliteCommand command,
        SemanticProfile profile)
    {
        command.Parameters.AddWithValue(
            "$profileId",
            profile.ProfileId);
        command.Parameters.AddWithValue(
            "$providerId",
            profile.Provider.ProviderId);
        command.Parameters.AddWithValue(
            "$modelId",
            profile.Provider.ModelId);
        command.Parameters.AddWithValue(
            "$dimension",
            profile.Provider.Dimension);
        command.Parameters.AddWithValue(
            "$maxInputTokens",
            profile.Provider.MaxInputTokens);
        command.Parameters.AddWithValue(
            "$profileVersion",
            profile.ProfileVersion);
        command.Parameters.AddWithValue(
            "$chunkerVersion",
            profile.ChunkerVersion);
        command.Parameters.AddWithValue(
            "$endpointFingerprint",
            profile.Provider.EndpointFingerprint);
        command.Parameters.AddWithValue(
            "$isRemote",
            profile.Provider.IsRemote ? 1 : 0);
        command.Parameters.AddWithValue(
            "$status",
            profile.Status.ToDatabaseValue());
        command.Parameters.AddWithValue(
            "$lastErrorCode",
            profile.LastErrorCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$createdAt",
            profile.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$updatedAt",
            profile.UpdatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$readyAt",
            profile.ReadyAt?.ToUniversalTime().ToString("O")
            ?? (object)DBNull.Value);
    }

    private static SemanticProfile ReadProfile(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            new EmbeddingProviderDescriptor(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(7),
                reader.GetInt32(8) == 1),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(9) == 1,
            SemanticValueMapping.ParseProfileStatus(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            ParseTimestamp(reader.GetString(12)),
            ParseTimestamp(reader.GetString(13)),
            reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
}
