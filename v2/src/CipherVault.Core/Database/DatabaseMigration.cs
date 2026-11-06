using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Database;

internal sealed record DatabaseMigration(
    int Version,
    string Name,
    string Sql,
    string ChecksumSha256,
    IReadOnlyList<string> LegacyChecksumSha256);

internal static class DatabaseMigrationCatalog
{
    public const int CurrentVersion = 1;
    private const string LegacyBaselineChecksumSha256 =
        "0ff5c23e6c5caa63e85f2d8c0febc0b09377426908a6ad70899b42ce4df12ae0";

    public static IReadOnlyList<DatabaseMigration> Load()
    {
        var assembly = typeof(DatabaseMigrationCatalog).Assembly;
        const string resourcePrefix = "CipherVault.Core.Database.Migrations.";
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name =>
                name.StartsWith(resourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (resourceNames.Length != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Expected {CurrentVersion} embedded migrations but found {resourceNames.Length}.");
        }

        var migrations = new List<DatabaseMigration>(resourceNames.Length);
        foreach (var resourceName in resourceNames)
        {
            var fileName = resourceName[resourcePrefix.Length..];
            var stem = fileName[..^4];
            var separator = stem.IndexOf('_');
            if (separator <= 0
                || !int.TryParse(
                    stem[..separator],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var version)
                || version != migrations.Count + 1)
            {
                throw new InvalidOperationException(
                    $"Migration resource name is invalid or out of sequence: {fileName}");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Migration resource was not found: {resourceName}");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();
            var canonicalSql = sql
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var checksum = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSql)))
                .ToLowerInvariant();
            migrations.Add(new DatabaseMigration(
                version,
                stem[(separator + 1)..],
                sql,
                checksum,
                version == 1 ? [LegacyBaselineChecksumSha256] : []));
        }

        return migrations;
    }
}
