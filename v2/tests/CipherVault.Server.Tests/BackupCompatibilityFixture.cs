using System.Security.Cryptography;
using System.Text.Json;

namespace CipherVault.Server.Tests;

internal sealed record BackupCompatibilityFixtureMetadata(
    string Format,
    int FormatVersion,
    string FixtureFileName,
    long FixtureBytes,
    string FixtureSha256,
    string SoftwareVersion,
    string GitCommit,
    DateTimeOffset GeneratedAtUtc,
    int DatabaseSchemaVersion,
    int BackupFormatVersion,
    string TestPassword,
    IReadOnlyList<BackupCompatibilityFixtureDocument> Documents);

internal sealed record BackupCompatibilityFixtureDocument(
    string Title,
    string Path,
    string Format,
    int Revision,
    IReadOnlyList<string> Tags,
    string ContentSha256);

internal static class BackupCompatibilityFixture
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string PackagePath => System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "BackupCompatibility",
        "schema-5-format-1.cvbackup");

    internal static string MetadataPath => System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "BackupCompatibility",
        "schema-5-format-1.json");

    internal static async Task<BackupCompatibilityFixtureMetadata> ReadMetadataAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(MetadataPath);
        return await JsonSerializer.DeserializeAsync<BackupCompatibilityFixtureMetadata>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Backup compatibility fixture metadata could not be read.");
    }

    internal static async Task VerifyPackageIdentityAsync(
        BackupCompatibilityFixtureMetadata metadata,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(PackagePath);
        Assert.True(file.Exists, "Backup compatibility fixture is missing.");
        Assert.Equal(metadata.FixtureBytes, file.Length);
        var hash = Convert.ToHexString(
            SHA256.HashData(
                await File.ReadAllBytesAsync(PackagePath, cancellationToken)))
            .ToLowerInvariant();
        Assert.Equal(metadata.FixtureSha256, hash);
    }
}
