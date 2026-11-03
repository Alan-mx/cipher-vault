using System.Text.Json;
using CipherVault.Core.Database;

namespace CipherVault.Core.Tests;

public sealed class MigrationChecksumBaselineTests
{
    [Fact]
    public async Task PublishedMigrationChecksumsMatchPinnedBaseline()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Migrations",
            "migration-checksums.json");
        await using var stream = File.OpenRead(fixturePath);
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        Assert.Equal(
            "cipher-vault-migration-checksums",
            root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());

        var expected = root.GetProperty("migrations")
            .EnumerateArray()
            .Select(item => new
            {
                Version = item.GetProperty("version").GetInt32(),
                Name = item.GetProperty("name").GetString(),
                Sha256 = item.GetProperty("sha256").GetString()
            })
            .ToArray();
        var actual = DatabaseMigrationCatalog.Load();

        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Version, actual[index].Version);
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(
                expected[index].Sha256,
                actual[index].ChecksumSha256);
        }
    }
}
