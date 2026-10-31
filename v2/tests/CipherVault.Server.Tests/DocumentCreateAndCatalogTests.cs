using System.Net;
using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Tests;

public sealed class DocumentCreateAndCatalogTests
{
    [Fact]
    public async Task CreateDocumentReturnsLocationAndEtagAndAppearsInCatalog()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        using var created = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                "Server Note",
                null,
                "Notes",
                VaultDocumentFormat.Markdown,
                "# Server Note\nBody",
                ["Work"]));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.ToString());
        Assert.NotNull(created.Headers.Location);
        var json = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var documentId = json.RootElement.GetProperty("documentId").GetGuid();

        using var catalog = await server.Client.GetAsync(
            "/api/documents?folder=Notes&format=md&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        using var catalogJson = JsonDocument.Parse(
            await catalog.Content.ReadAsStringAsync());
        Assert.Single(catalogJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            documentId,
            catalogJson.RootElement
                .GetProperty("items")[0]
                .GetProperty("documentId")
                .GetGuid());
    }

    [Fact]
    public async Task CatalogRejectsInvalidQueryAndRequiresAuthentication()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var anonymousClient = server.CreateClient();
        using var unauthenticated = await anonymousClient.GetAsync(
            "/api/documents");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        using var invalid = await server.Client.GetAsync(
            "/api/documents?format=pdf");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task CatalogFiltersTagsWithAndSemanticsAndBindsCursorToFilters()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        _ = await CreateAsync(server, token, "Both", ["Work", "Project"]);
        _ = await CreateAsync(server, token, "Work Only", ["Work"]);
        _ = await CreateAsync(server, token, "Personal", ["Personal"]);

        using var filtered = await server.Client.GetAsync(
            "/api/documents?tags=Work&tags=Project&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        using var filteredJson = JsonDocument.Parse(
            await filtered.Content.ReadAsStringAsync());
        var items = filteredJson.RootElement.GetProperty("items")
            .EnumerateArray()
            .ToArray();
        Assert.Single(items);
        Assert.Equal("Both", items[0].GetProperty("title").GetString());

        using var duplicate = await server.Client.GetAsync(
            "/api/documents?tags=Work&tags=work");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        using var firstPage = await server.Client.GetAsync(
            "/api/documents?tags=Work&pageSize=1");
        using var firstPageJson = JsonDocument.Parse(
            await firstPage.Content.ReadAsStringAsync());
        var cursor = firstPageJson.RootElement
            .GetProperty("nextCursor")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        using var wrongFilters = await server.Client.GetAsync(
            $"/api/documents?tags=Personal&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, wrongFilters.StatusCode);
    }

    [Fact]
    public async Task OrphanCatalogUsesActiveResolvedIncomingLinksOnly()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        _ = await CreateAsync(server, token, "Target", []);
        var sourceId = await CreateAsync(
            server,
            token,
            "Source",
            [],
            "[[Target]]");
        _ = await CreateAsync(server, token, "Self Link", [], "[[Self Link]]");
        _ = await CreateAsync(server, token, "Standalone", []);

        using var beforeDelete = await server.Client.GetAsync(
            "/api/documents?orphanOnly=true&pageSize=100");
        var beforeDeleteText = await beforeDelete.Content.ReadAsStringAsync();
        Assert.True(beforeDelete.IsSuccessStatusCode, beforeDeleteText);
        using var beforeDeleteJson = JsonDocument.Parse(
            beforeDeleteText);
        var beforeTitles = beforeDeleteJson.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("title").GetString())
            .ToArray();
        Assert.DoesNotContain("Target", beforeTitles);
        Assert.Contains("Source", beforeTitles);
        Assert.Contains("Self Link", beforeTitles);
        Assert.Contains("Standalone", beforeTitles);

        using var deleted = await server.Client.SendWithRevisionAsync<object>(
            HttpMethod.Delete,
            $"/api/documents/{sourceId:D}",
            token,
            revision: 1);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        using var afterDelete = await server.Client.GetAsync(
            "/api/documents?orphanOnly=true&pageSize=100");
        var afterDeleteText = await afterDelete.Content.ReadAsStringAsync();
        Assert.True(afterDelete.IsSuccessStatusCode, afterDeleteText);
        using var afterDeleteJson = JsonDocument.Parse(
            afterDeleteText);
        var afterTitles = afterDeleteJson.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("title").GetString())
            .ToArray();
        Assert.Contains("Target", afterTitles);
        Assert.Contains("Standalone", afterTitles);
        Assert.DoesNotContain("Source", afterTitles);
    }

    private static async Task<Guid> CreateAsync(
        ServerTestApplication server,
        string token,
        string title,
        IReadOnlyList<string> tags,
        string? content = null)
    {
        using var response = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                title,
                null,
                null,
                VaultDocumentFormat.Markdown,
                content ?? $"# {title}",
                tags));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("documentId").GetGuid();
    }

}
