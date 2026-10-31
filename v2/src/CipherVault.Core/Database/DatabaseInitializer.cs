using System.Globalization;
using CipherVault.Core.Errors;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Database;

public sealed record VaultDatabaseInfo(
    Guid VaultId,
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc);

internal static class DatabaseInitializer
{
    private const string CreateMigrationTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            checksum_sha256 TEXT NOT NULL CHECK (length(checksum_sha256) = 64),
            applied_at TEXT NOT NULL
        ) STRICT;
        """;

    internal static Task<VaultDatabaseInfo> EnsureSchemaAsync(
        SqliteConnection connection,
        Guid vaultId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken) =>
        EnsureSchemaAsync(
            connection,
            vaultId,
            createdAtUtc,
            DatabaseMigrationCatalog.Load(),
            cancellationToken);

    internal static async Task<VaultDatabaseInfo> EnsureSchemaAsync(
        SqliteConnection connection,
        Guid vaultId,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<DatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        if (vaultId == Guid.Empty)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Vault ID must not be empty.");
        }

        var targetVersion = migrations.Count == 0
            ? 0
            : migrations.Max(item => item.Version);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
            connection,
            CreateMigrationTableSql,
            cancellationToken,
            transaction);

        var appliedVersion = await ReadAppliedVersionAsync(connection, transaction, cancellationToken);
        if (appliedVersion is null)
        {
            if (await TableExistsAsync(connection, transaction, "vault_meta", cancellationToken))
            {
                throw new VaultException(
                    VaultErrorCode.CorruptVault,
                    "Vault metadata exists without migration history.");
            }

        }
        else
        {
            ValidateKnownVersion(appliedVersion.Value, targetVersion);
            await ValidateMigrationHistoryAsync(connection, transaction, migrations, cancellationToken);
        }

        var nextVersion = (appliedVersion ?? 0) + 1;
        foreach (var migration in migrations
                     .Where(item => item.Version >= nextVersion)
                     .OrderBy(item => item.Version))
        {
            if (migration.Version != nextVersion)
            {
                throw new VaultException(
                    VaultErrorCode.CorruptVault,
                    "Vault migration sequence contains a gap.");
            }

            await ApplyMigrationAsync(
                connection,
                transaction,
                migration,
                vaultId,
                createdAtUtc,
                cancellationToken);
            nextVersion++;
        }

        var info = await ReadInfoAsync(connection, transaction, cancellationToken);
        if (info.VaultId != vaultId)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Vault key does not belong to this database.");
        }

        var finalAppliedVersion = await ReadAppliedVersionAsync(connection, transaction, cancellationToken);
        if (info.SchemaVersion != finalAppliedVersion)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Vault schema version is inconsistent with migration history.");
        }

        if (info.SchemaVersion != targetVersion)
        {
            throw new VaultException(
                VaultErrorCode.UnsupportedVersion,
                "Vault schema version does not match the migration catalog.");
        }

        await transaction.CommitAsync(cancellationToken);
        return info;
    }

    internal static async Task<VaultDatabaseInfo> ReadAndValidateInfoAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction: null, "vault_meta", cancellationToken)
            || !await TableExistsAsync(connection, transaction: null, "schema_migrations", cancellationToken))
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Vault schema is incomplete.");
        }

        var info = await ReadInfoAsync(connection, transaction: null, cancellationToken);
        if (info.SchemaVersion > DatabaseMigrationCatalog.CurrentVersion)
        {
            throw new VaultException(
                VaultErrorCode.UnsupportedVersion,
                "Vault schema is newer than this application.");
        }

        var appliedVersion = await ReadAppliedVersionAsync(connection, transaction: null, cancellationToken);
        if (appliedVersion != info.SchemaVersion)
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Vault schema version is inconsistent.");
        }

        await ValidateMigrationHistoryAsync(
            connection,
            transaction: null,
            DatabaseMigrationCatalog.Load(),
            cancellationToken);
        return info;
    }

    internal static async Task VerifyFts5Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
            connection,
            """
            CREATE VIRTUAL TABLE temp.phase1_fts_probe USING fts5(value);
            INSERT INTO temp.phase1_fts_probe(value) VALUES ('fts5 available');
            DROP TABLE temp.phase1_fts_probe;
            """,
            cancellationToken);
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseMigration migration,
        Guid vaultId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = migration.Sql;
            command.Parameters.AddWithValue("$vaultId", vaultId.ToString("D").ToLowerInvariant());
            command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum_sha256, applied_at)
                VALUES ($version, $name, $checksum, $appliedAt);
                INSERT INTO vault_meta(key, value) VALUES ('schema_version', $versionText)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$name", migration.Name);
            command.Parameters.AddWithValue("$checksum", migration.ChecksumSha256);
            command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$versionText", migration.Version.ToString(CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ValidateMigrationHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<DatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        var expected = migrations.ToDictionary(item => item.Version);
        var appliedVersions = new HashSet<int>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, name, checksum_sha256 FROM schema_migrations ORDER BY version;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var version = reader.GetInt32(0);
            appliedVersions.Add(version);
            if (!expected.TryGetValue(version, out var migration)
                || !string.Equals(
                    reader.GetString(1),
                    migration.Name,
                    StringComparison.Ordinal)
                || !string.Equals(
                    reader.GetString(2),
                    migration.ChecksumSha256,
                    StringComparison.Ordinal))
            {
                throw new VaultException(
                    VaultErrorCode.CorruptVault,
                    "Vault migration history is invalid.");
            }
        }

        var maximumApplied = appliedVersions.Count == 0 ? 0 : appliedVersions.Max();
        for (var version = 1; version <= maximumApplied; version++)
        {
            if (!appliedVersions.Contains(version))
            {
                throw new VaultException(
                    VaultErrorCode.CorruptVault,
                    "Vault migration history is incomplete.");
            }
        }
    }

    private static async Task<VaultDatabaseInfo> ReadInfoAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var vaultIdText = await ReadMetaAsync(connection, transaction, "vault_id", cancellationToken);
        var versionText = await ReadMetaAsync(connection, transaction, "schema_version", cancellationToken);
        var createdAtText = await ReadMetaAsync(connection, transaction, "created_at", cancellationToken);
        if (!Guid.TryParse(vaultIdText, out var vaultId)
            || !int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || !DateTimeOffset.TryParse(createdAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Vault metadata is invalid.");
        }

        return new VaultDatabaseInfo(vaultId, version, createdAt.ToUniversalTime());
    }

    private static async Task<int?> ReadAppliedVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadMetaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM vault_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) > 0;
    }

    private static void ValidateKnownVersion(int version, int targetVersion)
    {
        if (version > targetVersion)
        {
            throw new VaultException(
                VaultErrorCode.UnsupportedVersion,
                "Vault schema is newer than this application.");
        }
    }
}
