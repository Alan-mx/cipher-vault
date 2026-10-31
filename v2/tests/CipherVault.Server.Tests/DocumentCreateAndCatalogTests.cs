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
}
