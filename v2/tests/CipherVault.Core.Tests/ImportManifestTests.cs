using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Core.Exports;
using CipherVault.Core.Import;

namespace CipherVault.Core.Tests;

public sealed class ImportManifestTests
{
    [Fact]
    public async Task ReadsValidManifestV2()
    {
        var root = CreateRoot();
        try
        {
            var id = Guid.NewGuid();
            await WriteManifestAsync(
                root,
                CreateManifest(
                    id,
                    "notes/a.md",
                    "Title",
                    "md",
                    "# Body"));

            var manifest = await ImportManifestReader.ReadAsync(
                root,
                CancellationToken.None);

            Assert.NotNull(manifest);
            var item = Assert.Single(manifest.DocumentsByPath).Value;
            Assert.Equal(id, item.DocumentId);
            Assert.Equal("notes/a.md", item.VirtualPath);
            Assert.Equal("Title", item.Title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsManifestV1WithStableCode()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, PlaintextExportLimits.ManifestFileName),
                """{"format":"cipher-vault-plaintext-export","formatVersion":1,"documentCount":0,"totalContentBytes":0,"documents":[]}""");

            var exception = await Assert.ThrowsAsync<VaultException>(
                () => ImportManifestReader.ReadAsync(
                    root,
                    CancellationToken.None));

            Assert.Equal(VaultErrorCode.UnsupportedManifestVersion, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsDuplicateIdsAndPaths()
    {
        var root = CreateRoot();
        try
        {
            var id = Guid.NewGuid();
            var first = ManifestDocument(id, "a.md", "A", "md", "A");
            var duplicateId = ManifestDocument(id, "b.md", "B", "md", "B");
            await WriteManifestAsync(
                root,
                ManifestJson([first, duplicateId]));

            var idError = await Assert.ThrowsAsync<VaultException>(
                () => ImportManifestReader.ReadAsync(
                    root,
                    CancellationToken.None));
            Assert.Equal(VaultErrorCode.InvalidManifest, idError.Code);

            await WriteManifestAsync(
                root,
                ManifestJson([
                    ManifestDocument(Guid.NewGuid(), "a.md", "A", "md", "A"),
                    ManifestDocument(Guid.NewGuid(), "a.md", "B", "md", "B")
                ]));
            var pathError = await Assert.ThrowsAsync<VaultException>(
                () => ImportManifestReader.ReadAsync(
                    root,
                    CancellationToken.None));
            Assert.Equal(VaultErrorCode.InvalidManifest, pathError.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateManifest(
        Guid id,
        string path,
        string title,
        string format,
        string content) =>
        ManifestJson([
            ManifestDocument(id, path, title, format, content)
        ]);

    private static object ManifestDocument(
        Guid id,
        string path,
        string title,
        string format,
        string content)
    {
        var bytes = Encoding.UTF8.GetByteCount(content);
        return new
        {
            documentId = id.ToString("D"),
            virtualPath = path,
            title,
            format,
            revision = 1,
            bytes,
            sha256 = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                .ToLowerInvariant()
        };
    }

    private static string ManifestJson(IReadOnlyList<object> documents)
    {
        var entries = documents
            .Select(document => JsonSerializer.SerializeToElement(document))
            .ToArray();
        var total = entries.Sum(item =>
            item.GetProperty("bytes").GetInt64());
        return JsonSerializer.Serialize(
            new
            {
                format = PlaintextExportLimits.ManifestFormat,
                formatVersion = PlaintextExportLimits.ManifestFormatVersion,
                exportedAtUtc = "2026-11-04T00:00:00Z",
                documentCount = entries.Length,
                totalContentBytes = total,
                documents = entries
            });
    }

    private static Task WriteManifestAsync(string root, string json) =>
        File.WriteAllTextAsync(
            Path.Combine(root, PlaintextExportLimits.ManifestFileName),
            json);
}
