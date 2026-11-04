using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Server.Api;
using CipherVault.Server.Api.Contracts;

namespace CipherVault.Server.Tests;

public sealed class SemanticSearchApiTests
{
    [Fact]
    public async Task SearchModesDefaultKeywordAndDegradeHybridWithoutProvider()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var keyword = await server.Client.PostAsJsonAsync(
            "/api/search/query",
            new { query = "any" });
        var keywordBody = await keyword.Content.ReadAsStringAsync();
        Assert.True(keyword.IsSuccessStatusCode, keywordBody);
        using var keywordJson = await ReadJsonAsync(keyword);
        Assert.Equal(
            "keyword",
            keywordJson.RootElement.GetProperty("mode").GetString());
        Assert.False(
            keywordJson.RootElement.GetProperty("degraded").GetBoolean());

        using var semantic = await server.Client.PostAsJsonAsync(
            "/api/search/query",
            new { query = "any", searchMode = "semantic" });
        Assert.Equal(HttpStatusCode.Conflict, semantic.StatusCode);
        Assert.Equal(
            ApiErrorCodes.SemanticSearchUnavailable,
            await ReadErrorCodeAsync(semantic));
        using (var semanticError = await ReadJsonAsync(semantic))
        {
            Assert.Equal(
                "not_configured",
                semanticError.RootElement
                    .GetProperty("details")
                    .GetProperty("reasonCode")
                    .GetString());
        }

        using var hybrid = await server.Client.PostAsJsonAsync(
            "/api/search/query",
            new { query = "any", searchMode = "hybrid" });
        Assert.Equal(HttpStatusCode.OK, hybrid.StatusCode);
        using var hybridJson = await ReadJsonAsync(hybrid);
        Assert.Equal(
            "hybrid",
            hybridJson.RootElement.GetProperty("mode").GetString());
        Assert.True(
            hybridJson.RootElement.GetProperty("degraded").GetBoolean());
        Assert.Equal(
            "not_configured",
            hybridJson.RootElement.GetProperty("degradedReason").GetString());

        using var status = await server.Client.GetAsync(
            "/api/semantic-index/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.True(status.Headers.CacheControl?.NoStore);
        using var statusJson = await ReadJsonAsync(status);
        Assert.Equal(
            "not_configured",
            statusJson.RootElement.GetProperty("state").GetString());

        using var emptySemantic = await server.Client.PostAsJsonAsync(
            "/api/search/query",
            new
            {
                query = "",
                searchMode = "semantic",
                tags = new[] { "any" }
            });
        Assert.Equal(HttpStatusCode.BadRequest, emptySemantic.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(emptySemantic));

        using var invalidMode = await server.Client.PostAsJsonAsync(
            "/api/search/query",
            new { query = "any", searchMode = "unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidMode.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(invalidMode));
    }

    [Fact]
    public async Task SemanticManagementRequiresCsrfAndUuidIdempotencyKey()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var body = new
        {
            profileId = new string('a', 64),
            endpointFingerprint = new string('b', 64)
        };

        using var missingCsrf = await server.Client.PostAsJsonAsync(
            "/api/semantic-index/remote-consent/grant",
            body);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal(
            ApiErrorCodes.AntiforgeryInvalid,
            await ReadErrorCodeAsync(missingCsrf));

        var token = await server.Client.GetAntiforgeryTokenAsync();
        using var missingKey = await server.Client
            .PostJsonWithAntiforgeryAsync(
                "/api/semantic-index/remote-consent/grant",
                token,
                body);
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        Assert.Equal(
            ApiErrorCodes.IdempotencyKeyRequired,
            await ReadErrorCodeAsync(missingKey));
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response)
    {
        using var json = await ReadJsonAsync(response);
        return json.RootElement.GetProperty("code").GetString();
    }
}
