using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Server.Api;
using CipherVault.Server.Api.Contracts;

namespace CipherVault.Server.Tests;

public sealed class DraftApiTests
{
    [Fact]
    public async Task WriteModeApiRequiresCsrfAndIdempotency()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var initial = await server.Client.GetAsync(
            "/api/vault/session/mcp-write-mode");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var initialJson = JsonDocument.Parse(
            await initial.Content.ReadAsStringAsync());
        Assert.Equal(
            "draft",
            initialJson.RootElement.GetProperty("mode").GetString());

        using var withoutCsrf = await server.Client.PutAsJsonAsync(
            "/api/vault/session/mcp-write-mode",
            new McpWriteModeApiRequest("direct"));
        Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);

        var token = await server.Client.GetAntiforgeryTokenAsync();
        using var withoutKey = await SendJsonAsync(
            server.Client,
            HttpMethod.Put,
            "/api/vault/session/mcp-write-mode",
            token,
            idempotencyKey: null,
            new McpWriteModeApiRequest("direct"));
        Assert.Equal(HttpStatusCode.BadRequest, withoutKey.StatusCode);
        Assert.Equal(
            ApiErrorCodes.IdempotencyKeyRequired,
            await ReadErrorCodeAsync(withoutKey));

        var key = Guid.NewGuid();
        using var direct = await SendJsonAsync(
            server.Client,
            HttpMethod.Put,
            "/api/vault/session/mcp-write-mode",
            token,
            key,
            new McpWriteModeApiRequest("direct"));
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);

        using var replay = await SendJsonAsync(
            server.Client,
            HttpMethod.Put,
            "/api/vault/session/mcp-write-mode",
            token,
            key,
            new McpWriteModeApiRequest("direct"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using var reused = await SendJsonAsync(
            server.Client,
            HttpMethod.Put,
            "/api/vault/session/mcp-write-mode",
            token,
            key,
            new McpWriteModeApiRequest("draft"));
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal(
            ApiErrorCodes.IdempotencyKeyReused,
            await ReadErrorCodeAsync(reused));
    }

    [Fact]
    public async Task DraftCanBeCreatedReviewedAndAppliedThroughApi()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        using var payloadDocument = JsonDocument.Parse(
            """
            {
              "title": "Draft API",
              "virtualPath": "notes/draft-api.md",
              "format": "md",
              "content": "# Draft API\n",
              "tags": []
            }
            """);
        var createKey = Guid.NewGuid();
        using var create = await SendJsonAsync(
            server.Client,
            HttpMethod.Post,
            "/api/drafts",
            token,
            createKey,
            new CreateDocumentDraftApiRequest(
                "create",
                null,
                null,
                payloadDocument.RootElement.Clone()));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await ReadJsonAsync(create);
        var draftId = created.RootElement.GetProperty("draftId").GetGuid();
        var revision = created.RootElement.GetProperty("draftRevision").GetInt32();

        using var page = await server.Client.GetAsync(
            "/api/drafts?status=draft");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        using var pageJson = JsonDocument.Parse(
            await page.Content.ReadAsStringAsync());
        Assert.Contains(
            pageJson.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("draftId").GetGuid() == draftId);

        using var review = await SendJsonAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{draftId:D}/submit-review",
            token,
            Guid.NewGuid(),
            new DocumentDraftRevisionApiRequest(revision));
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var reviewed = await ReadJsonAsync(review);
        revision = reviewed.RootElement.GetProperty("draftRevision").GetInt32();

        using var apply = await SendJsonAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{draftId:D}/apply",
            token,
            Guid.NewGuid(),
            new DocumentDraftRevisionApiRequest(revision));
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        var applied = await ReadJsonAsync(apply);
        var documentId = applied.RootElement
            .GetProperty("documentWriteResult")
            .GetProperty("documentId")
            .GetGuid();

        using var document = await server.Client.GetAsync(
            $"/api/documents/{documentId:D}");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
    }

    private static Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpClient client,
        HttpMethod method,
        string path,
        string csrfToken,
        Guid? idempotencyKey,
        T value)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(value)
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        if (idempotencyKey is not null)
        {
            request.Headers.Add(
                "Idempotency-Key",
                idempotencyKey.Value.ToString("D"));
        }
        return client.SendAsync(request);
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
