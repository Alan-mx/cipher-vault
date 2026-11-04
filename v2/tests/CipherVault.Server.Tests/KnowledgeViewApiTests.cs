using System.Net;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Server.Api;

namespace CipherVault.Server.Tests;

public sealed class KnowledgeViewApiTests
{
    [Fact]
    public async Task ProjectFacetsApplyActiveOnlyAndSetNoStore()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        _ = await CreateAsync(
            server.Client,
            token,
            "Project A Upper",
            "project: Project A\nstatus: active");
        _ = await CreateAsync(
            server.Client,
            token,
            "Project A Lower",
            "project: project a\nstatus: active");
        _ = await CreateAsync(
            server.Client,
            token,
            "Project B",
            "project: Project B\nstatus: planned");
        _ = await CreateAsync(
            server.Client,
            token,
            "Missing Project",
            "status: active");

        using var response = await server.Client.GetAsync(
            "/api/knowledge/views/project/facets?activeOnly=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal("project", json.RootElement.GetProperty("field").GetString());
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
        var items = json.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("PROJECT A", items[0].GetProperty("key").GetString());
        Assert.Equal("Project A", items[0].GetProperty("label").GetString());
        Assert.Equal(2, items[0].GetProperty("documentCount").GetInt32());
        Assert.False(items[0].GetProperty("isUnset").GetBoolean());
        Assert.True(items[1].GetProperty("isUnset").GetBoolean());
        Assert.Equal(
            JsonValueKind.Null,
            items[1].GetProperty("key").ValueKind);
        Assert.Equal(1, items[1].GetProperty("documentCount").GetInt32());
    }

    [Fact]
    public async Task FacetsRejectInvalidParametersWithStableErrorAndNoStore()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        var cases = new[]
        {
            "/api/knowledge/views/project/facets?activeOnly=invalid",
            "/api/knowledge/views/project/facets?status=active",
            "/api/knowledge/views/type/facets?activeOnly=false",
            "/api/knowledge/views/unknown/facets"
        };
        foreach (var path in cases)
        {
            using var response = await server.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal(
                ApiErrorCodes.InvalidInput,
                await ReadErrorCodeAsync(response));
        }
    }

    [Fact]
    public async Task UnauthorizedFacetRequestStillUsesNoStore()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var anonymous = server.CreateClient();

        using var response = await anonymous.GetAsync(
            "/api/knowledge/views/project/facets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task DocumentsFilterPageAndRejectInvalidParameterCombinations()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        _ = await CreateAsync(
            server.Client,
            token,
            "View A One",
            "project: Project A\nstatus: active\ntags: [Work]");
        _ = await CreateAsync(
            server.Client,
            token,
            "View A Two",
            "project: project a\nstatus: active\ntags: [Work]");
        _ = await CreateAsync(
            server.Client,
            token,
            "View A Planned",
            "project: Project A\nstatus: planned");
        _ = await CreateAsync(
            server.Client,
            token,
            "View Missing",
            "status: active");
        _ = await CreateAsync(
            server.Client,
            token,
            "View Blank",
            "project: \"   \"\nstatus: active");

        using var first = await server.Client.GetAsync(
            "/api/knowledge/views/project/documents"
            + "?facetKey=project%20a&activeOnly=true&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        using var firstJson = JsonDocument.Parse(
            await first.Content.ReadAsStringAsync());
        Assert.Single(firstJson.RootElement.GetProperty("items").EnumerateArray());
        var item = firstJson.RootElement.GetProperty("items")[0];
        Assert.False(item.TryGetProperty("content", out _));
        Assert.False(item.TryGetProperty("snippet", out _));
        Assert.Equal(["Work"], item.GetProperty("tags")
            .EnumerateArray()
            .Select(tag => tag.GetString()));
        var cursor = firstJson.RootElement
            .GetProperty("nextCursor")
            .GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        using var second = await server.Client.GetAsync(
            "/api/knowledge/views/project/documents"
            + "?facetKey=PROJECT%20A&activeOnly=true&pageSize=1"
            + $"&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondJson = JsonDocument.Parse(
            await second.Content.ReadAsStringAsync());
        Assert.Single(secondJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            JsonValueKind.Null,
            secondJson.RootElement.GetProperty("nextCursor").ValueKind);

        using var unset = await server.Client.GetAsync(
            "/api/knowledge/views/project/documents?unset=true&activeOnly=true");
        Assert.Equal(HttpStatusCode.OK, unset.StatusCode);
        using var unsetJson = JsonDocument.Parse(
            await unset.Content.ReadAsStringAsync());
        Assert.Equal(
            2,
            unsetJson.RootElement.GetProperty("items").GetArrayLength());

        var invalidPaths = new[]
        {
            "/api/knowledge/views/project/documents",
            "/api/knowledge/views/project/documents?facetKey=A&unset=true",
            "/api/knowledge/views/project/documents?facetKey=A&unset=false",
            "/api/knowledge/views/type/documents?facetKey=NOTE&activeOnly=false",
            "/api/knowledge/views/project/documents?facetKey=A&status=active",
            "/api/knowledge/views/project/documents?facetKey=A&pageSize=abc",
            "/api/knowledge/views/project/documents?facetKey=A&pageSize=0",
            "/api/knowledge/views/project/documents?facetKey=A&pageSize=101",
            "/api/knowledge/views/unknown/documents?facetKey=A"
        };
        foreach (var path in invalidPaths)
        {
            using var invalid = await server.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(
                ApiErrorCodes.InvalidInput,
                await ReadErrorCodeAsync(invalid));
        }

        using var invalidCursor = await server.Client.GetAsync(
            "/api/knowledge/views/project/documents"
            + "?facetKey=PROJECT%20A&activeOnly=false&pageSize=1"
            + $"&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidCursor,
            await ReadErrorCodeAsync(invalidCursor));

        using var emptyCursor = await server.Client.GetAsync(
            "/api/knowledge/views/project/documents?facetKey=A&cursor=");
        Assert.Equal(HttpStatusCode.BadRequest, emptyCursor.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidCursor,
            await ReadErrorCodeAsync(emptyCursor));
    }

    private static async Task<Guid> CreateAsync(
        HttpClient client,
        string token,
        string title,
        string frontMatter)
    {
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                title,
                null,
                null,
                VaultDocumentFormat.Markdown,
                $"""
                 ---
                 {frontMatter}
                 ---

                 Body.
                 """,
                null));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("documentId").GetGuid();
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }
}
