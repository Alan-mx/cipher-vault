using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Server.Api;
using CipherVault.Server.Api.Contracts;

namespace CipherVault.Server.Tests;

public sealed class PropertySearchApiTests
{
    [Fact]
    public async Task StructuredSearchSupportsEmptyQueryAndPropertyFilters()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        await CreateAsync(
            server.Client,
            token,
            "Verified",
            """
            ---
            status: verified
            source:
              kind: project
            ---

            needle
            """);
        await CreateAsync(
            server.Client,
            token,
            "Raw",
            """
            ---
            status: raw
            source:
              kind: conversation
            ---

            needle
            """);

        using var value = JsonDocument.Parse("\"project\"");
        using var response = await server.Client.PostAsJsonAsync(
            "/api/search/query",
            new StructuredSearchApiRequest(
                Query: "",
                Tags: null,
                Format: null,
                UpdatedAfter: null,
                UpdatedBefore: null,
                HasBacklinks: null,
                OrphanOnly: false,
                PropertyMatch: "all",
                PropertyFilters:
                [
                    new SearchPropertyFilterApiRequest(
                        "source.kind",
                        "eq",
                        value.RootElement.Clone())
                ],
                PageSize: 20,
                Cursor: null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Single(json.RootElement.GetProperty("hits").EnumerateArray());
        Assert.Equal(
            "Verified",
            json.RootElement
                .GetProperty("hits")[0]
                .GetProperty("title")
                .GetString());

        using var simple = await server.Client.GetAsync(
            "/api/search?query=needle");
        Assert.Equal(HttpStatusCode.OK, simple.StatusCode);
    }

    [Fact]
    public async Task StructuredSearchRejectsEmptyAndInvalidFilters()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var empty = await server.Client.PostAsJsonAsync(
            "/api/search/query",
            new StructuredSearchApiRequest(
                "",
                null,
                null,
                null,
                null,
                null,
                false,
                "all",
                null,
                20,
                null));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(empty));

        using var objectValue = JsonDocument.Parse("{}");
        using var invalid = await server.Client.PostAsJsonAsync(
            "/api/search/query",
            new StructuredSearchApiRequest(
                "",
                null,
                null,
                null,
                null,
                null,
                false,
                "all",
                [
                    new SearchPropertyFilterApiRequest(
                        "status",
                        "eq",
                        objectValue.RootElement.Clone())
                ],
                20,
                null));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(invalid));
    }

    private static async Task CreateAsync(
        HttpClient client,
        string token,
        string title,
        string content)
    {
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                title,
                $"notes/{title.ToLowerInvariant()}.md",
                null,
                VaultDocumentFormat.Markdown,
                content,
                null));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }
}
