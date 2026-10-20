using System.Reflection;
using Microsoft.Data.Sqlite;
using CipherVault.Core.Crypto;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using SQLitePCL;

namespace CipherVault.Core.Storage;

internal static class SqlCipherVaultStore
{
    private const string DatabaseFileName = "vault.db";
    private static int _initialized;

    public static void InitializeRuntime()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            Batteries_V2.Init();
        }
    }

    public static async Task InitializeAsync(
        string vaultDirectory,
        UnlockedKeyMaterial material,
        CancellationToken cancellationToken = default)
    {
        InitializeRuntime();
        var databasePath = Path.Combine(vaultDirectory, DatabaseFileName);
        using var connection = OpenConnection(databasePath, material.DbKey, create: true);
        ConfigureConnection(connection);

        var schema = await LoadSchemaAsync(cancellationToken).ConfigureAwait(false);
        Execute(connection, schema);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var transaction = connection.BeginTransaction();
        SetMeta(connection, transaction, "schema_version", "1");
        SetMeta(connection, transaction, "vault_id", material.VaultId.ToString("D"));
        SetMeta(connection, transaction, "created_at_ms", now.ToString());
        SetMeta(connection, transaction, "recovery_verified_at_ms", "0");
        SetMeta(connection, transaction, "last_cleanup_at_ms", now.ToString());
        transaction.Commit();
        VaultFileSecurity.ProtectFile(databasePath);
    }

    public static Task<SqliteConnection> OpenAsync(
        string vaultDirectory,
        UnlockedKeyMaterial material,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeRuntime();
        var databasePath = Path.Combine(vaultDirectory, DatabaseFileName);
        if (!File.Exists(databasePath))
        {
            throw new VaultException(VaultErrorCode.NotFound, "vault.db was not found.");
        }

        try
        {
            var connection = OpenConnection(databasePath, material.DbKey, create: false);
            ConfigureConnection(connection);
            VerifyVaultIdentity(connection, material.VaultId);
            VaultFileSecurity.ProtectFile(databasePath);
            return Task.FromResult(connection);
        }
        catch (SqliteException exception)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCredential,
                "Vault credential or database is invalid.",
                exception);
        }
    }

    public static async Task VerifyAsync(
        string vaultDirectory,
        UnlockedKeyMaterial material,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(vaultDirectory, material, cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(command.ExecuteScalar());
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Vault integrity check failed.");
        }
    }

    public static async Task CreateEncryptedSnapshotAsync(
        SqliteConnection source,
        string destinationPath,
        byte[] dbKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var destination = OpenConnection(destinationPath, dbKey, create: true);
        ConfigureConnection(destination);
        source.BackupDatabase(destination);
        destination.Close();
        await Task.Yield();
    }

    public static string GetCipherVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cipher_version;";
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    public static async Task<bool> IsRecoveryVerifiedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM vault_meta WHERE key = 'recovery_verified_at_ms';";
        var value = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        return long.TryParse(value, out var timestamp) && timestamp > 0;
    }

    private static SqliteConnection OpenConnection(string path, byte[] dbKey, bool create)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(dbKey)}'\";";
            command.ExecuteNonQuery();
            using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT count(*) FROM sqlite_master;";
            verify.ExecuteScalar();
            if (string.IsNullOrWhiteSpace(GetCipherVersion(connection)))
            {
                throw new VaultException(
                    VaultErrorCode.UnsupportedPlatform,
                    "SQLCipher runtime is unavailable.");
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA cipher_memory_security = ON;");
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "PRAGMA journal_mode = DELETE;");
        Execute(connection, "PRAGMA temp_store = MEMORY;");
        Execute(connection, "PRAGMA secure_delete = ON;");
        Execute(connection, "PRAGMA synchronous = FULL;");
    }

    private static void VerifyVaultIdentity(SqliteConnection connection, Guid expectedVaultId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM vault_meta WHERE key = 'vault_id';";
        var value = Convert.ToString(command.ExecuteScalar());
        if (!Guid.TryParse(value, out var actualVaultId) || actualVaultId != expectedVaultId)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "vault.keys does not belong to vault.db.");
        }
    }

    private static async Task<string> LoadSchemaAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(SqlCipherVaultStore).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("001_initial.sql", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded schema resource was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void SetMeta(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO vault_meta(key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
