using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Tests;

public sealed class DatabaseFactoryTests
{
    [Fact]
    public async Task RuntimeAndPragmasMatchFrozenConfiguration()
    {
        var runtime = await SqliteMcDatabaseFactory.InitializeProcessAsync();
        Assert.Contains("SQLite3 Multiple Ciphers 2.6.1", runtime.Sqlite3McVersion, StringComparison.Ordinal);
        Assert.Equal(2, runtime.TempStoreCompileValue);

        using var fixture = await DatabaseFixture.CreateAsync();
        await using var connection = await SqliteMcDatabaseFactory.OpenConnectionAsync(
            new DatabaseOpenRequest(fixture.Root, fixture.Material.DatabaseKey, CreateIfMissing: false));
        Assert.Equal("1", await ScalarAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("2", await ScalarAsync(connection, "PRAGMA temp_store;"));
        Assert.Equal("delete", await ScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal("ok", await ScalarAsync(connection, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task WrongKeyAndPlainDataAreRejected()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var wrongKeyException = await Assert.ThrowsAsync<VaultException>(async () =>
        {
            await using var connection = await SqliteMcDatabaseFactory.OpenConnectionAsync(
                new DatabaseOpenRequest(fixture.Root, wrongKey, CreateIfMissing: false));
        });
        Assert.Equal(VaultErrorCode.InvalidCredential, wrongKeyException.Code);
        CryptographicOperations.ZeroMemory(wrongKey);

        var plain = Encoding.ASCII.GetBytes("not a SQLite database");
        await File.WriteAllBytesAsync(fixture.DatabasePath, plain);

        var exception = await Assert.ThrowsAsync<VaultException>(async () =>
        {
            await using var connection = await SqliteMcDatabaseFactory.OpenConnectionAsync(
                new DatabaseOpenRequest(fixture.Root, fixture.Material.DatabaseKey, CreateIfMissing: false));
        });

        Assert.Equal(VaultErrorCode.InvalidCredential, exception.Code);
    }

    [Fact]
    public async Task CorrectKeyCanReadCreatedDatabase()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        await using var connection = await SqliteMcDatabaseFactory.OpenConnectionAsync(
            new DatabaseOpenRequest(fixture.Root, fixture.Material.DatabaseKey, CreateIfMissing: false));

        Assert.Equal("hello", await ScalarAsync(connection, "SELECT value FROM test_data WHERE id = 1;"));
    }

    [Fact]
    public async Task OrdinarySqliteProcessCannotOpenDatabase()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var v2Root = FindV2Root();
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var probe = Path.Combine(
            v2Root,
            "tests",
            "CipherVault.PlainSqliteProbe",
            "bin",
            configuration,
            "net10.0",
            "CipherVault.PlainSqliteProbe.dll");
        Assert.True(File.Exists(probe), $"Plain SQLite probe not found: {probe}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(probe);
        startInfo.ArgumentList.Add(fixture.DatabasePath);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("\"rejected\":true", await stdout, StringComparison.Ordinal);
        Assert.Empty(await stderr);
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static string FindV2Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CipherVault.slnx")))
            {
                return Path.Combine(directory.FullName, "v2");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("V2 solution root was not found.");
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private DatabaseFixture(string root, string databasePath, VaultKeyMaterial material)
        {
            Root = root;
            DatabasePath = databasePath;
            Material = material;
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public VaultKeyMaterial Material { get; }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-db-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            VaultFilePermissions.ProtectDirectory(root);
            var material = new VaultKeyMaterial(
                Guid.NewGuid(),
                RandomNumberGenerator.GetBytes(32),
                RandomNumberGenerator.GetBytes(32));
            var databasePath = Path.Combine(root, "vault.db");

            await using (var connection = await SqliteMcDatabaseFactory.OpenConnectionAsync(
                new DatabaseOpenRequest(root, material.DatabaseKey, CreateIfMissing: true)))
            {
                await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                    connection,
                    """
                    CREATE TABLE test_data(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO test_data(id, value) VALUES (1, 'hello');
                    """,
                    CancellationToken.None);
            }

            return new DatabaseFixture(root, databasePath, material);
        }

        public void Dispose()
        {
            Material.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
