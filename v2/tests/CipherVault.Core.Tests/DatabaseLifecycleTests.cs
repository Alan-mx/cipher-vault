using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Security;

namespace CipherVault.Core.Tests;

public sealed class DatabaseLifecycleTests
{
    [Fact]
    public async Task ShutdownIsSingleUseAndRejectsLaterOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-shutdown-{Guid.NewGuid():N}");
        var databaseKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var v2Root = FindV2Root();
            var configuration = AppContext.BaseDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
            var probe = Path.Combine(
                v2Root,
                "tests",
                "CipherVault.Phase1.ProcessProbe",
                "bin",
                configuration,
                "net10.0",
                "CipherVault.Phase1.ProcessProbe.dll");
            Assert.True(File.Exists(probe), $"Process probe not found: {probe}");

            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(probe);
            startInfo.ArgumentList.Add("lifecycle");
            startInfo.ArgumentList.Add(root);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            await process.StandardInput.WriteLineAsync(Convert.ToHexString(databaseKey));
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var output = await stdout;
            var error = await stderr;

            Assert.True(process.ExitCode == 0, $"Probe failed: {output} {error}");
            Assert.Contains("\"shutdownCount\":1", output, StringComparison.Ordinal);
            Assert.Contains("\"reopenRejected\":true", output, StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(databaseKey);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TempPressureLeavesNoReadablePlaintextOrSidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-temp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        Environment.SetEnvironmentVariable("SQLITE_TMPDIR", root);
        var marker = $"CV_PHASE1_TEMP_{Guid.NewGuid():N}";
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using (var connection = await SqliteMcDatabaseFactory.OpenConnectionAsync(
                new DatabaseOpenRequest(root, material.DatabaseKey, CreateIfMissing: true)))
            {
                await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                    connection,
                    """
                    CREATE TABLE pressure(id INTEGER PRIMARY KEY, content TEXT NOT NULL);
                    CREATE VIRTUAL TABLE pressure_fts USING fts5(content, tokenize='trigram');
                    """,
                    CancellationToken.None);
                await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                    connection,
                    $"INSERT INTO pressure VALUES (1, '{marker}');",
                    CancellationToken.None);
                await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                    connection,
                    "INSERT INTO pressure_fts(content) SELECT content FROM pressure;",
                    CancellationToken.None);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM pressure ORDER BY content;";
                _ = await command.ExecuteScalarAsync();
            }

            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Assert.DoesNotContain(
                files,
                path => path.EndsWith("-journal", StringComparison.Ordinal)
                    || path.EndsWith("-wal", StringComparison.Ordinal)
                    || path.EndsWith("-shm", StringComparison.Ordinal));
            Assert.DoesNotContain(
                files,
                path => ContainsUtf8OrUtf16(File.ReadAllBytes(path), marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLITE_TMPDIR", null);
            Directory.Delete(root, recursive: true);
        }
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

    private static bool ContainsUtf8OrUtf16(byte[] content, string value) =>
        content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0
        || content.AsSpan().IndexOf(Encoding.Unicode.GetBytes(value)) >= 0
        || content.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes(value)) >= 0;
}
