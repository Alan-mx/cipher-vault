using System.Net;
using System.Text.Json;
using CipherVault.Server.Backup;
using CipherVault.Server.Hosting;

namespace CipherVault.Server.Tests;

public sealed class GitBackupStatusApiTests
{
    [Fact]
    public async Task RequiresAuthenticationAndUnlockedVault()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var anonymous = server.CreateClient();

        using var unauthorized = await anonymous.GetAsync(
            "/api/backups/git/status");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        using var locked = await server.CreateClient().GetAsync(
            "/api/backups/git/status");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [Fact]
    public async Task ReturnsDisabledStateWithoutSensitiveConfiguration()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var response = await server.Client.GetAsync(
            "/api/backups/git/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "disabled",
            json.RootElement.GetProperty("state").GetString());
        Assert.DoesNotContain(
            "path",
            json.RootElement.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "remote",
            json.RootElement.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsSafeFailedStateForInvalidRepository()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-status-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repository);
        try
        {
            await using var server = await ServerTestApplication.CreateAsync(
                initializeVault: true,
                gitBackup: GitBackupOptions.Disabled with
                {
                    Enabled = true,
                    RepositoryPath = repository
                });
            using var unlock = await server.Client.UnlockAsync();
            Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

            using var response = await server.Client.GetAsync(
                "/api/backups/git/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.Equal(
                "failed",
                json.RootElement.GetProperty("state").GetString());
            Assert.Equal(
                GitBackupErrorCodes.RepositoryInvalid,
                json.RootElement.GetProperty("lastErrorCode").GetString());
            Assert.DoesNotContain(
                repository,
                json.RootElement.GetRawText(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(repository))
            {
                Directory.Delete(repository, recursive: true);
            }
        }
    }
}
