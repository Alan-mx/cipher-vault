using System.Globalization;
using CipherVault.Core.Database;
using CipherVault.Core.Semantic;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage.Semantic;

public static class SemanticConsentStore
{
    public static async Task<SemanticRemoteConsent?> GetAsync(
        VaultOperationContext context,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                profile_id,
                consent_version,
                endpoint_fingerprint,
                granted_at,
                revoked_at,
                updated_at
            FROM semantic_remote_consents
            WHERE profile_id = $profileId;
            """;
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadConsent(reader)
            : null;
    }

    public static async Task GrantAsync(
        VaultOperationContext context,
        string profileId,
        string endpointFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO semantic_remote_consents(
                profile_id,
                consent_version,
                endpoint_fingerprint,
                granted_by_actor,
                granted_at,
                revoked_at,
                updated_at)
            VALUES(
                $profileId,
                1,
                $endpointFingerprint,
                'user_ui',
                $grantedAt,
                NULL,
                $updatedAt)
            ON CONFLICT(profile_id) DO UPDATE SET
                consent_version = excluded.consent_version,
                endpoint_fingerprint = excluded.endpoint_fingerprint,
                granted_by_actor = excluded.granted_by_actor,
                granted_at = excluded.granted_at,
                revoked_at = NULL,
                updated_at = excluded.updated_at;
            """;
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        command.Command.Parameters.AddWithValue(
            "$endpointFingerprint",
            endpointFingerprint);
        command.Command.Parameters.AddWithValue(
            "$grantedAt",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue(
            "$updatedAt",
            now.ToUniversalTime().ToString("O"));
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task RevokeAsync(
        VaultOperationContext context,
        string profileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            UPDATE semantic_remote_consents
            SET revoked_at = $revokedAt,
                updated_at = $updatedAt
            WHERE profile_id = $profileId
              AND revoked_at IS NULL;
            """;
        command.Command.Parameters.AddWithValue(
            "$revokedAt",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue(
            "$updatedAt",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SemanticRemoteConsent ReadConsent(
        SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3)),
            reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
}
