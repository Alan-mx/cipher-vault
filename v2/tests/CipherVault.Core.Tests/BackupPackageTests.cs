using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Backup;

namespace CipherVault.Core.Tests;

public sealed class BackupPackageTests
{
    [Fact]
    public async Task ValidPackagePassesPreflightAndManifestRead()
    {
        await using var fixture = await PackageFixture.CreateAsync();

        var preflight = await BackupZipPreflight.InspectAsync(
            fixture.Path,
            CancellationToken.None);
        var manifest = await BackupManifestReader.ReadAsync(
            fixture.Path,
            preflight,
            CancellationToken.None);

        Assert.Equal(3, preflight.Entries.Count);
        Assert.Equal(fixture.VaultId, manifest.VaultId);
        Assert.Equal(BackupConstants.Format, manifest.Format);
        Assert.Equal(2, manifest.Files.Count);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("folder\\escape")]
    public async Task UnsafeEntryNameIsRejected(string unsafeName)
    {
        await using var fixture = await PackageFixture.CreateAsync(
            databaseEntryName: unsafeName);

        var exception = await Assert.ThrowsAsync<BackupPackageValidationException>(
            () => BackupZipPreflight.InspectAsync(
                fixture.Path,
                CancellationToken.None));

        Assert.Equal("restore_archive_invalid", exception.Code);
    }

    [Fact]
    public async Task DuplicateEntryIsRejected()
    {
        await using var fixture = await PackageFixture.CreateAsync(
            duplicateManifest: true);

        var exception = await Assert.ThrowsAsync<BackupPackageValidationException>(
            () => BackupZipPreflight.InspectAsync(
                fixture.Path,
                CancellationToken.None));

        Assert.Equal("restore_archive_invalid", exception.Code);
    }

    [Fact]
    public async Task UnknownManifestFieldIsRejected()
    {
        await using var fixture = await PackageFixture.CreateAsync();
        fixture.ReplaceManifest("""
            {
              "format": "cipher-vault-backup",
              "formatVersion": 1,
              "vaultId": "11111111-1111-1111-1111-111111111111",
              "createdAtUtc": "2026-11-01T00:00:00.0000000Z",
              "files": [
                { "path": "vault.db", "bytes": 1, "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                { "path": "vault.key", "bytes": 1, "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
              ],
              "title": "secret"
            }
            """);

        var preflight = await BackupZipPreflight.InspectAsync(
            fixture.Path,
            CancellationToken.None);
        var exception = await Assert.ThrowsAsync<BackupPackageValidationException>(
            () => BackupManifestReader.ReadAsync(
                fixture.Path,
                preflight,
                CancellationToken.None));

        Assert.Equal("restore_manifest_invalid", exception.Code);
    }

    [Fact]
    public async Task ManifestFileSizesCannotOverflowLimits()
    {
        await using var fixture = await PackageFixture.CreateAsync();
        fixture.ReplaceManifest("""
            {
              "format": "cipher-vault-backup",
              "formatVersion": 1,
              "vaultId": "11111111-1111-1111-1111-111111111111",
              "createdAtUtc": "2026-11-01T00:00:00.0000000Z",
              "files": [
                { "path": "vault.db", "bytes": 9223372036854775807, "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                { "path": "vault.key", "bytes": 9223372036854775807, "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
              ]
            }
            """);

        var preflight = await BackupZipPreflight.InspectAsync(
            fixture.Path,
            CancellationToken.None);
        var exception = await Assert.ThrowsAsync<BackupPackageValidationException>(
            () => BackupManifestReader.ReadAsync(
                fixture.Path,
                preflight,
                CancellationToken.None));

        Assert.Equal("restore_manifest_invalid", exception.Code);
    }

    private sealed class PackageFixture : IAsyncDisposable
    {
        private readonly string root;

        private PackageFixture(string root, Guid vaultId)
        {
            this.root = root;
            VaultId = vaultId;
            Path = System.IO.Path.Combine(
                root,
                "cipher-vault-20261101-000000-deadbeef.cvbackup");
        }

        public string Path { get; }

        public Guid VaultId { get; }

        public static async Task<PackageFixture> CreateAsync(
            IReadOnlyList<string>? extraEntries = null,
            bool duplicateManifest = false,
            string? databaseEntryName = null)
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cipher-vault-package-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultId = Guid.Parse(
                "11111111-1111-1111-1111-111111111111");
            var fixture = new PackageFixture(root, vaultId);
            await using var stream = new FileStream(
                fixture.Path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None);
            using var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Create,
                leaveOpen: true);
            var database = Encoding.UTF8.GetBytes("encrypted-database");
            var key = Encoding.UTF8.GetBytes("key-envelope");
            var manifest = CreateManifest(vaultId, database, key);
            await WriteEntryAsync(
                archive,
                BackupConstants.ManifestEntryName,
                Encoding.UTF8.GetBytes(manifest));
            await WriteEntryAsync(
                archive,
                databaseEntryName ?? BackupConstants.DatabaseEntryName,
                database);
            await WriteEntryAsync(
                archive,
                BackupConstants.KeyEnvelopeEntryName,
                key);
            if (duplicateManifest)
            {
                await WriteEntryAsync(
                    archive,
                    BackupConstants.ManifestEntryName,
                    Encoding.UTF8.GetBytes(manifest));
            }
            foreach (var extra in extraEntries ?? [])
            {
                await WriteEntryAsync(
                    archive,
                    extra,
                    Encoding.UTF8.GetBytes("extra"));
            }
            return fixture;
        }

        public void ReplaceManifest(string json)
        {
            using var archive = ZipFile.Open(Path, ZipArchiveMode.Update);
            var existing = archive.GetEntry(BackupConstants.ManifestEntryName)
                ?? throw new InvalidOperationException();
            existing.Delete();
            var replacement = archive.CreateEntry(
                BackupConstants.ManifestEntryName,
                CompressionLevel.Optimal);
            using var writer = new StreamWriter(
                replacement.Open(),
                new UTF8Encoding(false));
            writer.Write(json);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }

        private static string CreateManifest(
            Guid vaultId,
            byte[] database,
            byte[] key) =>
            JsonSerializer.Serialize(new
            {
                format = BackupConstants.Format,
                formatVersion = BackupConstants.FormatVersion,
                vaultId = vaultId.ToString("D"),
                createdAtUtc = "2026-11-01T00:00:00.0000000Z",
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

        private static async Task WriteEntryAsync(
            ZipArchive archive,
            string name,
            byte[] content)
        {
            var entry = archive.CreateEntry(
                name,
                CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await stream.WriteAsync(content);
        }
    }
}
