using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Database;

internal sealed record DatabaseMigration(
    int Version,
    string Name,
    string Sql,
    string ChecksumSha256);

internal static class DatabaseMigrationCatalog
{
    public const int CurrentVersion = 5;

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
            var checksum = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
            migrations.Add(new DatabaseMigration(
                version,
                stem[(separator + 1)..],
                sql,
                checksum));
        }

        return migrations;
    }
}
