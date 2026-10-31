using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Application.Import;

namespace CipherVault.Server.Tests;

public sealed class ImportApiTests
{
    [Fact]
    public async Task PreviewAndCommitImportLocalFiles()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        var sourceRoot = Path.Combine(server.Parent, "import-source");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "Imported.md"),
            "# Imported\nbody");
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        using var preview = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/imports/preview",
            token,
            new ImportPreviewRequest(sourceRoot));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var previewJson = JsonDocument.Parse(
            await preview.Content.ReadAsStringAsync());
        Assert.Equal(1, previewJson.RootElement.GetProperty("readyCount").GetInt32());
        var importToken = previewJson.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(importToken));

        using var commit = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/imports/commit",
            token,
            new ImportCommitRequest(importToken!));
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        using var commitJson = JsonDocument.Parse(
            await commit.Content.ReadAsStringAsync());
        Assert.Equal(1, commitJson.RootElement.GetProperty("imported").GetInt32());

        using var catalog = await server.Client.GetAsync("/api/documents");
        using var catalogJson = JsonDocument.Parse(
            await catalog.Content.ReadAsStringAsync());
        Assert.Single(catalogJson.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ImportRequiresAuthenticationAndCsrf()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        var sourceRoot = Path.Combine(server.Parent, "import-auth-source");
        Directory.CreateDirectory(sourceRoot);

        using var anonymousClient = server.CreateClient();
        using var unauthorized = await anonymousClient.PostJsonWithAntiforgeryAsync(
            "/api/imports/preview",
            "unused",
            new ImportPreviewRequest(sourceRoot));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        using var missingCsrf = await server.Client.PostAsJsonAsync(
            "/api/imports/preview",
            new ImportPreviewRequest(sourceRoot));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
    }
}
