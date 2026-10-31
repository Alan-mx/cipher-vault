using System.Net;
using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Tests;

public sealed class KnowledgeApiTests
{
    [Fact]
    public async Task TagsSearchAndLocalGraphAreExposed()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        var centerId = await CreateAsync(
            server.Client,
            token,
            "Center",
            "# Center\nneedle alpha [[Linked]]",
            ["Work"]);
        var linkedId = await CreateAsync(
            server.Client,
            token,
            "Linked",
            "# Linked\nLinked body",
            ["Work"]);
        _ = linkedId;

        using var tags = await server.Client.GetAsync("/api/tags");
        Assert.Equal(HttpStatusCode.OK, tags.StatusCode);
        using var tagJson = JsonDocument.Parse(
            await tags.Content.ReadAsStringAsync());
        Assert.Contains(
            tagJson.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "Work"
                && item.GetProperty("documentCount").GetInt32() == 2);

        using var search = await server.Client.GetAsync(
            "/api/search?query=needle&tags=Work&format=md");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var searchJson = JsonDocument.Parse(
            await search.Content.ReadAsStringAsync());
        Assert.Single(searchJson.RootElement.GetProperty("hits").EnumerateArray());

        using var graph = await server.Client.GetAsync(
            $"/api/graph/local?centerDocumentId={centerId:D}&depth=1");
        Assert.Equal(HttpStatusCode.OK, graph.StatusCode);
        using var graphJson = JsonDocument.Parse(
            await graph.Content.ReadAsStringAsync());
        Assert.Equal(
            2,
            graphJson.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(
            1,
            graphJson.RootElement.GetProperty("edges").GetArrayLength());

        using var oversizedGraph = await server.Client.GetAsync(
            $"/api/graph/local?centerDocumentId={centerId:D}&maxNodes=501");
        Assert.Equal(HttpStatusCode.BadRequest, oversizedGraph.StatusCode);
    }

    private static async Task<Guid> CreateAsync(
        HttpClient client,
        string token,
        string title,
        string content,
        IReadOnlyList<string>? tags)
    {
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                title,
                null,
                null,
                VaultDocumentFormat.Markdown,
                content,
                tags));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("documentId").GetGuid();
    }
}
