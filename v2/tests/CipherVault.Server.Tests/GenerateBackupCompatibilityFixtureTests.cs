using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Server.Build;

namespace CipherVault.Server.Tests;

public sealed class GenerateBackupCompatibilityFixtureTests
{
    private const string FixtureFileName = "schema-5-format-1.cvbackup";
    private const string FixtureDirectoryName = "BackupCompatibility";
    private const string FixtureContent =
        "# Compatibility Fixture\n\nThis is synthetic data for backup compatibility tests.";

    [Fact(Skip = "Manual fixture generation only. Remove this skip only when intentionally creating the baseline fixture.")]
    public async Task GenerateSchema5BackupFixture()
    {
        var fixtureDirectory = Path.Combine(
            FindRepositoryRoot(),
            "v2",
            "tests",
            "Fixtures",
            FixtureDirectoryName);
        Directory.CreateDirectory(fixtureDirectory);
        var fixturePath = Path.Combine(fixtureDirectory, FixtureFileName);
        if (File.Exists(fixturePath))
        {
            throw new InvalidOperationException(
                "The frozen backup fixture already exists. Do not regenerate it.");
        }

        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var antiforgery = await server.Client.GetAntiforgeryTokenAsync();
        using var created = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            antiforgery,
            new CreateDocumentRequest(
                "Compatibility Fixture",
                "fixtures/compatibility-fixture.md",
                null,
                VaultDocumentFormat.Markdown,
                FixtureContent,
                ["compatibility", "fixture"]));
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
        using var createdJson = JsonDocument.Parse(createdBody);
        var documentId = createdJson.RootElement
            .GetProperty("documentId")
            .GetGuid();

        using var metadataResponse = await server.Client.GetAsync(
            $"/api/documents/{documentId:D}");
        var metadataBody = await metadataResponse.Content.ReadAsStringAsync();
        Assert.True(metadataResponse.IsSuccessStatusCode, metadataBody);
        using var metadata = JsonDocument.Parse(metadataBody);

        using var contentResponse = await server.Client.GetAsync(
            $"/api/documents/{documentId:D}/content");
        var contentBody = await contentResponse.Content.ReadAsStringAsync();
        Assert.True(contentResponse.IsSuccessStatusCode, contentBody);
        using var content = JsonDocument.Parse(contentBody);
        var storedContent = content.RootElement
            .GetProperty("content")
            .GetString()!;

        using var backup = await server.Client.PostWithAntiforgeryAsync(
            "/api/backups",
            antiforgery);
        var backupBody = await backup.Content.ReadAsStringAsync();
        Assert.True(backup.StatusCode == HttpStatusCode.Created, backupBody);
        using var backupJson = JsonDocument.Parse(backupBody);
        var packageFileName = backupJson.RootElement
            .GetProperty("backup")
            .GetProperty("fileName")
            .GetString()!;
        var packagePath = Path.Combine(
            server.Options.Paths.BackupDirectory,
            packageFileName);
        File.Copy(packagePath, fixturePath);

        var fixtureBytes = new FileInfo(fixturePath).Length;
        var fixtureHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(fixturePath)))
            .ToLowerInvariant();
        var contentHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(storedContent)))
            .ToLowerInvariant();
        var build = BuildMetadataReader.Read();
        var generatedAt = DateTimeOffset.UtcNow;
        var metadataPath = Path.Combine(
            fixtureDirectory,
            "schema-5-format-1.json");
        var metadataJson = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                format = "cipher-vault-backup-compatibility-fixture",
                formatVersion = 1,
                fixtureFileName = FixtureFileName,
                fixtureBytes,
                fixtureSha256 = fixtureHash,
                softwareVersion = build.ProductVersion,
                gitCommit = build.Commit,
                generatedAtUtc = generatedAt,
                databaseSchemaVersion = 5,
                backupFormatVersion = BackupConstants.FormatVersion,
                testPassword = ServerTestApplication.Password,
                documents = new[]
                {
                    new
                    {
                        title = metadata.RootElement
                            .GetProperty("title")
                            .GetString(),
                        path = metadata.RootElement
                            .GetProperty("virtualPath")
                            .GetString(),
                        format = "md",
                        revision = metadata.RootElement
                            .GetProperty("revision")
                            .GetInt32(),
                        tags = metadata.RootElement
                            .GetProperty("tags")
                            .EnumerateArray()
                            .Select(item => item.GetString())
                            .ToArray(),
                        contentSha256 = contentHash
                    }
                }
            },
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        await File.WriteAllBytesAsync(metadataPath, metadataJson);

        var verifyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-fixture-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(verifyDirectory);
        try
        {
            using var credential =
                new InteractivePasswordCredential(ServerTestApplication.Password);
            var verified = await BackupRestoreStore.ExtractAndValidateAsync(
                fixturePath,
                verifyDirectory,
                credential,
                CancellationToken.None);
            Assert.Equal(5, verified.SchemaVersion);
            Assert.Equal(BackupConstants.FormatVersion, verified.FormatVersion);
        }
        finally
        {
            Directory.Delete(verifyDirectory, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the CipherVault repository root.");
    }
}
