using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class BackupCreateApiTests
{
    [Fact]
    public async Task CreateBackupPublishesVerifiedPackageAndListsIt()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var antiforgery = await server.Client.GetAntiforgeryTokenAsync();

        using var response = await server.Client.PostWithAntiforgeryAsync(
            "/api/backups",
            antiforgery);

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            responseBody);
        Assert.NotNull(response.Headers.Location);
        using var json = JsonDocument.Parse(responseBody);
        var item = json.RootElement.GetProperty("backup");
        var fileName = item.GetProperty("fileName").GetString()!;
        Assert.Equal("verified", item.GetProperty("status").GetString());
        var packagePath = Path.Combine(
            server.Options.Paths.BackupDirectory,
            fileName);
        Assert.True(File.Exists(packagePath));
        using (var archive = ZipFile.OpenRead(packagePath))
        {
            Assert.Equal(
                [
                    "manifest.json",
                    "vault.db",
                    "vault.key"
                ],
                archive.Entries
                    .Select(entry => entry.FullName)
                    .Order(StringComparer.Ordinal));
        }

        using var listed = await server.Client.GetAsync("/api/backups");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var listJson = JsonDocument.Parse(
            await listed.Content.ReadAsStringAsync());
        Assert.Equal(
            "verified",
            listJson.RootElement.GetProperty("items")[0]
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    public async Task CreateBackupRejectsBodyAndMissingCsrf()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var antiforgery = await server.Client.GetAntiforgeryTokenAsync();

        using var noCsrf = await server.Client.PostAsync(
            "/api/backups",
            content: null);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/backups")
        {
            Content = new StringContent("{}")
        };
        request.Headers.Add("X-CSRF-TOKEN", antiforgery);
        using var withBody = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, withBody.StatusCode);
        Assert.Contains(
            "invalid_input",
            await withBody.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }
}
