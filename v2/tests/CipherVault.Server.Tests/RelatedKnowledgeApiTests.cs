using System.Net;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Server.Api;

namespace CipherVault.Server.Tests;

public sealed class RelatedKnowledgeApiTests
{
    [Fact]
    public async Task RelatedDocumentsUseBoundedContractAndExcludeDirectLinks()
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
            "Related Project",
            "---\nproject: CipherVault\n---\n\nRelated.");
        _ = await CreateAsync(
            server.Client,
            token,
            "Direct Project",
            "---\nproject: CipherVault\n---\n\nDirect.");
        var source = await CreateAsync(
            server.Client,
            token,
            "Source",
            """
            ---
            project: CipherVault
            ---

            [[Direct Project]]
            """);

        using var response = await server.Client.GetAsync(
            $"/api/documents/{source.DocumentId:D}/related");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("ETag"));
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["items"],
            json.RootElement.EnumerateObject().Select(item => item.Name));
        var items = json.RootElement.GetProperty("items");
        var item = Assert.Single(items.EnumerateArray());
        Assert.Equal("Related Project", item.GetProperty("title").GetString());
        Assert.False(item.TryGetProperty("content", out _));
        Assert.False(item.TryGetProperty("snippet", out _));
        Assert.False(json.RootElement.TryGetProperty("nextCursor", out _));
        Assert.NotEmpty(item.GetProperty("reasons").EnumerateArray());

        using var limited = await server.Client.GetAsync(
            $"/api/documents/{source.DocumentId:D}/related?limit=1");
        Assert.Equal(HttpStatusCode.OK, limited.StatusCode);
        using var limitedJson = JsonDocument.Parse(
            await limited.Content.ReadAsStringAsync());
        Assert.Equal(
            1,
            limitedJson.RootElement.GetProperty("items").GetArrayLength());

        using var maximum = await server.Client.GetAsync(
            $"/api/documents/{source.DocumentId:D}/related?limit=20");
        Assert.Equal(HttpStatusCode.OK, maximum.StatusCode);
    }

    [Fact]
    public async Task RelatedDocumentsUseStableLimitMissingAndTrashErrors()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        var trashed = await CreateAsync(
            server.Client,
            token,
            "Trashed",
            "Trashed.");

        using var zero = await server.Client.GetAsync(
            $"/api/documents/{trashed.DocumentId:D}/related?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidInput, await ReadErrorCodeAsync(zero));

        using var oversized = await server.Client.GetAsync(
            $"/api/documents/{trashed.DocumentId:D}/related?limit=21");
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(oversized));

        using var missing = await server.Client.GetAsync(
            $"/api/documents/{Guid.NewGuid():D}/related");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            ApiErrorCodes.DocumentNotFound,
            await ReadErrorCodeAsync(missing));

        using var delete = await server.Client.SendWithRevisionAsync<object>(
            HttpMethod.Delete,
            $"/api/documents/{trashed.DocumentId:D}",
            token,
            trashed.Revision);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        using var deleted = await server.Client.GetAsync(
            $"/api/documents/{trashed.DocumentId:D}/related");
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        Assert.Equal(
            ApiErrorCodes.DocumentNotFound,
            await ReadErrorCodeAsync(deleted));
    }

    [Fact]
    public async Task RelatedDocumentsUseExistingLockedContract()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        var source = await CreateAsync(
            server.Client,
            token,
            "Locked Source",
            "Locked.");

        _ = await server.Manager.LockAsync();
        using var response = await server.Client.GetAsync(
            $"/api/documents/{source.DocumentId:D}/related");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            ApiErrorCodes.AuthenticationRequired,
            await ReadErrorCodeAsync(response));
    }

    private static async Task<(Guid DocumentId, int Revision)> CreateAsync(
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
                null,
                null,
                VaultDocumentFormat.Markdown,
                content,
                null));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return (
            json.RootElement.GetProperty("documentId").GetGuid(),
            json.RootElement.GetProperty("revision").GetInt32());
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }
}
