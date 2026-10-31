using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Database;

internal sealed record DatabaseMigration(
    int Version,
    string Name,
    string Sql,
    string ChecksumSha256);

internal static class DatabaseMigrationCatalog
{
    public const int CurrentVersion = 1;

    public static IReadOnlyList<DatabaseMigration> Load()
    {
        var assembly = typeof(DatabaseMigrationCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Database.Migrations.001_initial.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Initial migration resource was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sql = reader.ReadToEnd();
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
        return [new DatabaseMigration(1, "initial", sql, checksum)];
    }
}
