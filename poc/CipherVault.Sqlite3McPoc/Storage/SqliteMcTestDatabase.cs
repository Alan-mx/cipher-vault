using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CipherVault.Phase0;

public static class SqliteMcTestDatabase
{
    private static int _initialized;
    private static int _shutdown;

    public static async Task<SqliteRuntimeInfo> InitializeProcessAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            Batteries_V2.Init();
        }

        await using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        await connection.OpenAsync(cancellationToken);
        var version = await ExecuteScalarStringAsync(
            connection,
            "SELECT sqlite3mc_version();",
            cancellationToken) ?? "unknown";
        var compileTempStore = await ReadCompileTempStoreAsync(connection, cancellationToken);
        var nativeLibrary = FindSqlite3McLibrary();
        return new SqliteRuntimeInfo(version, compileTempStore, null, nativeLibrary);
    }

    public static async Task CreateAndSeedAsync(
        string dbPath,
        ReadOnlyMemory<byte> key,
        Phase0TestData testData,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenEncryptedAsync(dbPath, key, cancellationToken);
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "schema", "phase0.sql");
        var schema = await File.ReadAllTextAsync(schemaPath, cancellationToken);
        await ExecuteNonQueryAsync(connection, schema, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var largeText = testData.AsciiSentinel + " " + testData.ChineseSentinel + " "
            + new string('x', 256 * 1024) + " CV_PHASE0_TAIL";

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO documents(id, title, content, created_at)
                VALUES (1, $title, $content, $createdAt);
                """;
            command.Parameters.AddWithValue("$title", "睡眠记录 phasezeroftsmarker");
            command.Parameters.AddWithValue("$content", largeText);
            command.Parameters.AddWithValue("$createdAt", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 2; index <= 129; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO documents(id, title, content, created_at)
                VALUES ($id, $title, $content, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", index);
            command.Parameters.AddWithValue("$title", $"sort marker {129 - index:D3}");
            command.Parameters.AddWithValue(
                "$content",
                $"{new string((char)('a' + (index % 26)), 2048)} sort-{index:D3}");
            command.Parameters.AddWithValue("$createdAt", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            "INSERT INTO documents_fts(rowid, title, content) SELECT id, title, content FROM documents;",
            cancellationToken,
            (SqliteTransaction)transaction);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO crash_probe(id, marker, payload, write_count, updated_at)
                VALUES (1, 'BASELINE', 'baseline payload', 0, $updatedAt);
                """;
            command.Parameters.AddWithValue("$updatedAt", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var runtimeValue = await ReadPragmaIntegerAsync(connection, "temp_store", cancellationToken);
        if (runtimeValue != 2)
        {
            throw new InvalidOperationException($"temp_store expected 2, actual {runtimeValue?.ToString() ?? "null"}.");
        }
    }

    public static async Task<SqliteReadResult> VerifyReadableAsync(
        string dbPath,
        ReadOnlyMemory<byte> key,
        Phase0TestData testData,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenEncryptedAsync(dbPath, key, cancellationToken);
            var integrity = await ExecuteScalarStringAsync(connection, "PRAGMA integrity_check;", cancellationToken);
            var ascii = await ContainsValueAsync(connection, testData.AsciiSentinel, cancellationToken);
            var chinese = await ContainsValueAsync(connection, testData.ChineseSentinel, cancellationToken);
            var ftsEnglish = await CountFtsAsync(connection, testData.FtsEnglishMarker, cancellationToken);
            var ftsChinese = await CountFtsAsync(connection, testData.FtsChineseMarker, cancellationToken);
            return new SqliteReadResult(
                string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase),
                ascii,
                chinese,
                ftsEnglish,
                ftsChinese,
                null,
                null);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            return new SqliteReadResult(
                false,
                false,
                false,
                0,
                0,
                exception.GetType().Name,
                Sanitize(exception.Message));
        }
    }

    public static async Task<WrongKeyProbeResult> VerifyWrongKeyRejectedAsync(
        string dbPath,
        ReadOnlyMemory<byte> wrongKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenEncryptedAsync(dbPath, wrongKey, cancellationToken, createIfMissing: false);
            await ExecuteScalarStringAsync(connection, "SELECT count(*) FROM sqlite_master;", cancellationToken);
            return new WrongKeyProbeResult(false, null, null, "Wrong key unexpectedly opened the database.");
        }
        catch (SqliteException exception)
        {
            return new WrongKeyProbeResult(
                true,
                exception.GetType().Name,
                exception.SqliteErrorCode,
                Sanitize(exception.Message));
        }
    }

    public static async Task<TamperProbeResult> VerifyTamperRejectedAsync(
        string sourcePath,
        string tamperPath,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        File.Copy(sourcePath, tamperPath, overwrite: true);
        var fileLength = new FileInfo(tamperPath).Length;
        var offset = Math.Min(4096L + 128L, fileLength - 1);
        await using (var stream = new FileStream(tamperPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = offset;
            var original = stream.ReadByte();
            stream.Position = offset;
            stream.WriteByte((byte)(original ^ 0x5A));
            await stream.FlushAsync(cancellationToken);
        }

        try
        {
            await using var connection = await OpenEncryptedAsync(tamperPath, key, cancellationToken, createIfMissing: false);
            var integrity = await ExecuteScalarStringAsync(connection, "PRAGMA integrity_check;", cancellationToken);
            var readable = await ExecuteScalarStringAsync(connection, "SELECT count(*) FROM documents;", cancellationToken);
            var rejected = !string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase);
            return new TamperProbeResult(
                rejected,
                offset,
                null,
                rejected ? $"integrity_check={integrity}" : $"Tampered database remained readable. rows={readable}");
        }
        catch (SqliteException exception)
        {
            return new TamperProbeResult(
                true,
                offset,
                exception.GetType().Name,
                Sanitize(exception.Message));
        }
    }

    public static async Task<DatabaseHeaderEvidence> ReadHeaderEvidenceAsync(
        string dbPath,
        ReadOnlyMemory<byte> key,
        Phase0TestData testData,
        CancellationToken cancellationToken)
    {
        long pageSize;
        long pageCount;
        long schemaVersion;
        long applicationId;
        long userVersion;
        await using (var connection = await OpenEncryptedAsync(dbPath, key, cancellationToken, createIfMissing: false))
        {
            pageSize = await ReadPragmaIntegerAsync(connection, "page_size", cancellationToken) ?? 0;
            pageCount = await ReadPragmaIntegerAsync(connection, "page_count", cancellationToken) ?? 0;
            schemaVersion = await ReadPragmaIntegerAsync(connection, "schema_version", cancellationToken) ?? 0;
            applicationId = await ReadPragmaIntegerAsync(connection, "application_id", cancellationToken) ?? 0;
            userVersion = await ReadPragmaIntegerAsync(connection, "user_version", cancellationToken) ?? 0;
        }

        var bytes = await File.ReadAllBytesAsync(dbPath, cancellationToken);
        var first100 = bytes.AsSpan(0, Math.Min(100, bytes.Length));
        var firstPageLength = (int)Math.Min(pageSize <= 0 ? 4096 : pageSize, bytes.Length);
        var firstPage = bytes.AsSpan(0, firstPageLength);
        var containsSentinel =
            ContainsBytes(firstPage, Encoding.UTF8.GetBytes(testData.AsciiSentinel))
            || ContainsBytes(firstPage, Encoding.UTF8.GetBytes(testData.ChineseSentinel));

        return new DatabaseHeaderEvidence(
            Convert.ToHexString(bytes.AsSpan(0, Math.Min(24, bytes.Length))),
            Convert.ToHexString(SHA256.HashData(first100)),
            Convert.ToHexString(SHA256.HashData(firstPage)),
            bytes.LongLength,
            pageSize,
            pageCount,
            schemaVersion,
            applicationId,
            userVersion,
            containsSentinel);
    }

    public static async Task RunTempPressureAsync(
        string dbPath,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenEncryptedAsync(dbPath, key, cancellationToken, createIfMissing: false);
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TEMP TABLE temp_sort AS
            SELECT id, title, lower(content) AS normalized
            FROM documents
            ORDER BY content, title;

            CREATE TEMP TABLE temp_group AS
            SELECT substr(normalized, 1, 32) AS prefix, count(*) AS item_count
            FROM temp_sort
            GROUP BY prefix
            ORDER BY prefix;
            """,
            cancellationToken);
        _ = await ExecuteScalarStringAsync(
            connection,
            "SELECT count(*) FROM temp_sort;",
            cancellationToken);
        _ = await ExecuteScalarStringAsync(
            connection,
            "SELECT count(*) FROM documents_fts WHERE documents_fts MATCH 'phasezeroftsmarker';",
            cancellationToken);
    }

    public static async Task CreateEncryptedBackupAsync(
        string sourcePath,
        string backupPath,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        await using var source = await OpenEncryptedAsync(sourcePath, key, cancellationToken, createIfMissing: false);
        await using var destination = await OpenEncryptedAsync(backupPath, key, cancellationToken);
        source.BackupDatabase(destination);
    }

    public static Task<SqliteReadResult> VerifyBackupAsync(
        string backupPath,
        ReadOnlyMemory<byte> key,
        Phase0TestData testData,
        CancellationToken cancellationToken) =>
        VerifyReadableAsync(backupPath, key, testData, cancellationToken);

    public static async Task WriteUncommittedCrashMarkerAndWaitAsync(
        string dbPath,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenEncryptedAsync(dbPath, key, cancellationToken, createIfMissing: false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE crash_probe
            SET marker = 'CV_CRASH_UNCOMMITTED',
                payload = $payload,
                write_count = 999,
                updated_at = $updatedAt
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$payload", new string('z', 128 * 1024));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        Console.Out.WriteLine("CV_PHASE0_CRASH_READY");
        await Console.Out.FlushAsync(cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public static async Task<string?> ReadCrashMarkerAsync(
        string dbPath,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenEncryptedAsync(dbPath, key, cancellationToken, createIfMissing: false);
        return await ExecuteScalarStringAsync(
            connection,
            "SELECT marker FROM crash_probe WHERE id = 1;",
            cancellationToken);
    }

    public static async Task CompleteCrashRecoveryAsync(
        string dbPath,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenEncryptedAsync(dbPath, key, cancellationToken, createIfMissing: false);
        await ExecuteNonQueryAsync(
            connection,
            """
            BEGIN IMMEDIATE;
            UPDATE crash_probe SET write_count = write_count WHERE id = 1;
            COMMIT;
            VACUUM;
            """,
            cancellationToken);
    }

    public static int ShutdownProcess()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return raw.SQLITE_OK;
        }

        return raw.sqlite3_shutdown();
    }

    private static async Task<SqliteConnection> OpenEncryptedAsync(
        string path,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken,
        bool createIfMissing = true)
    {
        if (!createIfMissing && !File.Exists(path))
        {
            throw new FileNotFoundException("Encrypted database does not exist.", path);
        }

        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA cipher = 'chacha20';", cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA encrypt_temp_db = ON;", cancellationToken);
            await ExecuteNonQueryAsync(
                connection,
                $"PRAGMA key = 'raw:{Convert.ToHexString(key.Span)}';",
                cancellationToken,
                redactSql: true);
            _ = await ExecuteScalarStringAsync(connection, "SELECT count(*) FROM sqlite_master;", cancellationToken);
            await ExecuteNonQueryAsync(
                connection,
                """
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = DELETE;
                PRAGMA synchronous = FULL;
                PRAGMA temp_store = MEMORY;
                PRAGMA secure_delete = ON;
                PRAGMA busy_timeout = 5000;
                """,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null,
        bool redactSql = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            var message = redactSql ? $"{exception.GetType().Name} while applying an encrypted key." : exception.Message;
            throw new InvalidOperationException(message, exception);
        }
    }

    private static async Task<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static async Task<bool> ContainsValueAsync(
        SqliteConnection connection,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM documents WHERE instr(content, $value) > 0;";
        command.Parameters.AddWithValue("$value", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<long> CountFtsAsync(
        SqliteConnection connection,
        string query,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM documents_fts WHERE documents_fts MATCH $query;";
        command.Parameters.AddWithValue("$query", query);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int?> ReadCompileTempStoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA compile_options;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var option = reader.GetString(0);
            if (option.StartsWith("SQLITE_TEMP_STORE=", StringComparison.Ordinal)
                || option.StartsWith("TEMP_STORE=", StringComparison.Ordinal))
            {
                var separator = option.IndexOf('=');
                return int.Parse(option[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static async Task<int?> ReadPragmaIntegerAsync(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken)
    {
        var value = await ExecuteScalarStringAsync(connection, $"PRAGMA {pragma};", cancellationToken);
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? FindSqlite3McLibrary()
    {
        if (!Directory.Exists(AppContext.BaseDirectory))
        {
            return null;
        }

        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var candidates = Directory.EnumerateFiles(AppContext.BaseDirectory, "sqlite3mc.*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Contains("pclraw", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return candidates.FirstOrDefault(path =>
                   path.Replace('\\', '/').Contains($"/runtimes/{rid}/native/", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(path =>
                new[] { ".dll", ".so", ".dylib" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    private static string Sanitize(string message)
    {
        var rawIndex = message.IndexOf("raw:", StringComparison.OrdinalIgnoreCase);
        if (rawIndex >= 0)
        {
            var suffixStart = message.IndexOf('\'', rawIndex);
            return suffixStart >= 0 ? message[..rawIndex] + "raw:<redacted>" + message[(suffixStart + 1)..] : "redacted";
        }

        return message.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static bool ContainsBytes(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return false;
        }

        return haystack.IndexOf(needle) >= 0;
    }
}
