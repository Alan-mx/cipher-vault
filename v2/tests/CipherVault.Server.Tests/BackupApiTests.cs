using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Backup;

namespace CipherVault.Server.Tests;

public sealed class BackupApiTests
{
    [Fact]
    public async Task BackupListRequiresAuthenticationAndReturnsSafeMetadata()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        await CreatePackageAsync(
            server.Options.Paths.BackupDirectory,
            "cipher-vault-20261101-120000-deadbeef.cvbackup");

        using var anonymous = await server.CreateClient()
            .GetAsync("/api/backups");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        using var response = await server.Client.GetAsync("/api/backups");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var jsonText = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(
            server.Options.Paths.BackupDirectory,
            jsonText,
            StringComparison.Ordinal);
        using var json = JsonDocument.Parse(jsonText);
        var item = json.RootElement.GetProperty("items")[0];
        Assert.Equal(
            "cipher-vault-20261101-120000-deadbeef.cvbackup",
            item.GetProperty("fileName").GetString());
        Assert.Equal(
            "manifest_readable",
            item.GetProperty("status").GetString());
        Assert.Equal(
            1,
            item.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public async Task BackupListRejectsInvalidPageAndCursor()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var page = await server.Client.GetAsync(
            "/api/backups?pageSize=0");
        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        Assert.Contains(
            "invalid_input",
            await page.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var cursor = await server.Client.GetAsync(
            "/api/backups?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, cursor.StatusCode);
        Assert.Contains(
            "invalid_cursor",
            await cursor.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    private static async Task CreatePackageAsync(
        string root,
        string fileName)
    {
        Directory.CreateDirectory(root);
        var database = Encoding.UTF8.GetBytes("database");
        var key = Encoding.UTF8.GetBytes("key");
        var manifest = JsonSerializer.Serialize(new
        {
            format = BackupConstants.Format,
            formatVersion = BackupConstants.FormatVersion,
            vaultId = "11111111-1111-1111-1111-111111111111",
            createdAtUtc = "2026-10-30T12:00:00.0000000Z",
            files = new[]
            {
                new
                {
                    path = BackupConstants.DatabaseEntryName,
                    bytes = database.Length,
                    sha256 = Convert.ToHexString(
                        SHA256.HashData(database)).ToLowerInvariant()
                },
                new
                {
                    path = BackupConstants.KeyEnvelopeEntryName,
                    bytes = key.Length,
                    sha256 = Convert.ToHexString(
                        SHA256.HashData(key)).ToLowerInvariant()
                }
            }
        });
        await using var stream = new FileStream(
            Path.Combine(root, fileName),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        using var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: true);
        await WriteEntryAsync(
            archive,
            BackupConstants.ManifestEntryName,
            Encoding.UTF8.GetBytes(manifest));
        await WriteEntryAsync(
            archive,
            BackupConstants.DatabaseEntryName,
            database);
        await WriteEntryAsync(
            archive,
            BackupConstants.KeyEnvelopeEntryName,
            key);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(content);
    }
}
