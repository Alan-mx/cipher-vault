using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CipherVault.Core.Database;

public sealed record SqliteRuntimeInfo(
    string Sqlite3McVersion,
    int? TempStoreCompileValue);

internal sealed record DatabaseOpenRequest(
    string VaultDirectory,
    ReadOnlyMemory<byte> DatabaseKey,
    bool CreateIfMissing,
    bool ReadOnly = false);

public static class SqliteMcDatabaseFactory
{
    private static readonly object InitializationSync = new();
    private static Task<SqliteRuntimeInfo>? initializationTask;
    private static int shutdown;

    internal static int ShutdownCallCount => Volatile.Read(ref shutdown);

    public static async Task<SqliteRuntimeInfo> InitializeProcessAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfShutdown();
        Task<SqliteRuntimeInfo> task;
        lock (InitializationSync)
        {
            ThrowIfShutdown();
            initializationTask ??= InitializeCoreAsync();
            task = initializationTask;
        }

        return await task.WaitAsync(cancellationToken);
    }

    private static async Task<SqliteRuntimeInfo> InitializeCoreAsync()
    {
        var providerInitialized = false;
        try
        {
            Batteries_V2.Init();
            providerInitialized = true;
            await using var connection = new SqliteConnection(
                "Data Source=:memory:;Pooling=false");
            await connection.OpenAsync(CancellationToken.None);
            var version = await ExecuteScalarStringAsync(
                connection,
                "SELECT sqlite3mc_version();",
                CancellationToken.None);
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new VaultException(
                    VaultErrorCode.UnsupportedPlatform,
                    "SQLite3MC runtime is unavailable.");
            }

            var tempStore = await ReadCompileTempStoreAsync(
                connection,
                CancellationToken.None);
            if (tempStore is not (2 or 3))
            {
                throw new VaultException(
                    VaultErrorCode.DatabaseError,
                    "SQLite TEMP_STORE must be compiled as 2 or 3.");
            }

            return new SqliteRuntimeInfo(version, tempStore);
        }
        catch
        {
            if (providerInitialized)
            {
                try
                {
                    _ = raw.sqlite3_shutdown();
                }
                catch
                {
                    // Preserve the startup failure after best-effort cleanup.
                }
            }
            throw;
        }
    }

    public static async Task<SqliteMcDatabaseSession> OpenSessionAsync(
        string vaultDirectory,
        VaultKeyMaterial material,
        bool create,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        SqliteMcDatabaseSession? session = null;
        try
        {
            Directory.CreateDirectory(vaultDirectory);
            VaultFilePermissions.ProtectDirectory(vaultDirectory);
            session = await OpenUninitializedSessionAsync(vaultDirectory, material, create, cancellationToken);
            if (!create)
            {
                var integrity = await ExecuteScalarStringAsync(
                    session.Connection,
                    "PRAGMA integrity_check;",
                    cancellationToken);
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new VaultException(VaultErrorCode.CorruptVault, "Vault integrity check failed.");
                }
            }

            var info = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (info.VaultId != material.VaultId)
            {
                throw new VaultException(
                    VaultErrorCode.CorruptVault,
                    "Vault key does not belong to this database.");
            }

            await DatabaseInitializer.VerifyFts5Async(session.Connection, cancellationToken);
            var finalIntegrity = await ExecuteScalarStringAsync(
                session.Connection,
                "PRAGMA integrity_check;",
                cancellationToken);
            if (!string.Equals(finalIntegrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new VaultException(VaultErrorCode.CorruptVault, "Vault integrity check failed.");
            }

            session.DatabaseInfo = info;
            return session;
        }
        catch (VaultException)
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to access the Vault database.");
        }
        catch (OperationCanceledException)
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            throw;
        }
        catch (SqliteException exception)
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Vault database initialization failed.",
                exception);
        }
        catch (Exception exception)
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Vault database initialization failed.",
                exception);
        }
    }

    internal static async Task<SqliteMcDatabaseSession> OpenUninitializedSessionAsync(
        string vaultDirectory,
        VaultKeyMaterial material,
        bool create,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(
            new DatabaseOpenRequest(vaultDirectory, material.DatabaseKey, create),
            cancellationToken);
        return new SqliteMcDatabaseSession(material.VaultId, connection);
    }

    internal static async Task<SqliteConnection> OpenConnectionAsync(
        DatabaseOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = await InitializeProcessAsync(cancellationToken);
        if (request.DatabaseKey.Length != 32)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "DatabaseKey must be exactly 32 bytes.");
        }

        if (request.ReadOnly && request.CreateIfMissing)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Read-only database requests cannot create a database.");
        }

        var databasePath = Path.Combine(request.VaultDirectory, "vault.db");
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = request.ReadOnly
                    ? SqliteOpenMode.ReadOnly
                    : request.CreateIfMissing
                        ? SqliteOpenMode.ReadWriteCreate
                        : SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());

        var success = false;
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA cipher = 'chacha20';", cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA encrypt_temp_db = ON;", cancellationToken);
            await ExecuteNonQueryAsync(
                connection,
                $"PRAGMA key = 'raw:{Convert.ToHexString(request.DatabaseKey.Span)}';",
                cancellationToken,
                redactErrors: true);
            _ = await ExecuteScalarStringAsync(
                connection,
                "SELECT count(*) FROM sqlite_master;",
                cancellationToken);
            _ = await ExecuteScalarStringAsync(
                connection,
                "SELECT sqlite3mc_version();",
                cancellationToken);
            if (request.ReadOnly)
            {
                ConfigureReadOnlyConnection(connection);
            }
            else
            {
                ConfigureConnection(connection);
                VaultFilePermissions.ProtectFile(databasePath);
            }
            success = true;
            return connection;
        }
        catch (Exception exception)
        {
            throw MapOpenException(exception);
        }
        finally
        {
            if (!success)
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch
                {
                    // Preserve the original open/authentication failure.
                }
            }
        }
    }

    internal static void ConfigureConnection(SqliteConnection connection)
    {
        RegisterSearchFunctions(connection);
        ExecuteNonQuery(
            connection,
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = DELETE;
            PRAGMA synchronous = FULL;
            PRAGMA temp_store = MEMORY;
            PRAGMA secure_delete = ON;
            PRAGMA busy_timeout = 5000;
            """);
    }

    internal static async Task<SqliteConnection> OpenReadOnlyConnectionAsync(
        string vaultDirectory,
        ReadOnlyMemory<byte> databaseKey,
        CancellationToken cancellationToken = default) =>
        await OpenConnectionAsync(
            new DatabaseOpenRequest(
                vaultDirectory,
                databaseKey,
                CreateIfMissing: false,
                ReadOnly: true),
            cancellationToken);

    private static void ConfigureReadOnlyConnection(
        SqliteConnection connection)
    {
        RegisterSearchFunctions(connection);
        ExecuteNonQuery(
            connection,
            """
            PRAGMA foreign_keys = ON;
            PRAGMA temp_store = MEMORY;
            PRAGMA busy_timeout = 5000;
            PRAGMA query_only = ON;
            """);
    }

    private static void RegisterSearchFunctions(SqliteConnection connection)
    {
        connection.CreateFunction<string?, string?, long?>(
            "cv_ordinal_ignore_case_contains",
            (value, search) =>
                value is null || search is null
                    ? null
                    : value.Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase)
                        ? 1L
                        : 0L,
            isDeterministic: true);
    }

    public static int ShutdownProcess()
    {
        if (Interlocked.Exchange(ref shutdown, 1) != 0)
        {
            return raw.SQLITE_OK;
        }

        return raw.sqlite3_shutdown();
    }

    internal static void ExecuteNonQuery(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static void ThrowIfShutdown()
    {
        if (Volatile.Read(ref shutdown) != 0)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "SQLite3MC runtime has already been shut down.");
        }
    }

    private static Exception MapOpenException(Exception exception) =>
        exception switch
        {
            VaultException => exception,
            OperationCanceledException => exception,
            IOException or UnauthorizedAccessException or System.Security.SecurityException =>
                new VaultException(
                    VaultErrorCode.StorageError,
                    "Unable to access the Vault database."),
            SqliteException { SqliteErrorCode: 11 or 26 } =>
                new VaultException(
                    VaultErrorCode.InvalidCredential,
                    "Vault database credential is invalid.",
                    exception),
            SqliteException { SqliteErrorCode: 8 or 10 or 13 or 14 } =>
                new VaultException(
                    VaultErrorCode.StorageError,
                    "Unable to access the Vault database."),
            SqliteException =>
                new VaultException(
                    VaultErrorCode.DatabaseError,
                    "Vault database operation failed."),
            _ => new VaultException(
                VaultErrorCode.DatabaseError,
                "Vault database operation failed.")
        };

    private static async Task<int?> ReadCompileTempStoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA compile_options;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var option = reader.GetString(0);
            if (option.StartsWith("SQLITE_TEMP_STORE=", StringComparison.Ordinal)
                || option.StartsWith("TEMP_STORE=", StringComparison.Ordinal))
            {
                var separator = option.IndexOf('=');
                return int.Parse(
                    option[(separator + 1)..],
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        bool redactErrors)
    {
        try
        {
            await ExecuteNonQueryAsync(connection, sql, cancellationToken);
        }
        catch (Exception exception) when (redactErrors)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCredential,
                "Unable to apply the encrypted database key.",
                exception);
        }
    }
}
