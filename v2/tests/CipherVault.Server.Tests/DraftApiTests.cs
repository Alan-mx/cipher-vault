using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
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
            "/api/drafts?queue=working&status=draft");
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

    [Fact]
    public async Task DraftSummaryCountsAllQueuesAndDisablesCaching()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var empty = await server.Client.GetAsync("/api/drafts/summary");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.True(empty.Headers.CacheControl?.NoStore);
        using var emptyJson = await ReadJsonAsync(empty);
        Assert.Equal(0, emptyJson.RootElement.GetProperty("pendingReview").GetInt32());
        Assert.Equal(0, emptyJson.RootElement.GetProperty("working").GetInt32());
        Assert.Equal(0, emptyJson.RootElement.GetProperty("history").GetInt32());

        var token = await server.Client.GetAntiforgeryTokenAsync();
        var pending = await CreateDraftAsync(
            server.Client,
            token,
            "Pending summary");
        using var review = await SendJsonAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{pending.DraftId:D}/submit-review",
            token,
            Guid.NewGuid(),
            new DocumentDraftRevisionApiRequest(pending.Revision));
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);

        _ = await CreateDraftAsync(
            server.Client,
            token,
            "Working summary");
        var history = await CreateDraftAsync(
            server.Client,
            token,
            "History summary");
        using var discard = await SendJsonAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{history.DraftId:D}/discard",
            token,
            Guid.NewGuid(),
            new DocumentDraftRevisionApiRequest(history.Revision));
        Assert.Equal(HttpStatusCode.OK, discard.StatusCode);

        using var summary = await server.Client.GetAsync("/api/drafts/summary");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        using var summaryJson = await ReadJsonAsync(summary);
        Assert.Equal(
            1,
            summaryJson.RootElement.GetProperty("pendingReview").GetInt32());
        Assert.Equal(
            1,
            summaryJson.RootElement.GetProperty("working").GetInt32());
        Assert.Equal(
            1,
            summaryJson.RootElement.GetProperty("history").GetInt32());
    }

    [Fact]
    public async Task DraftListSupportsQueuesFiltersTimeAndCursorBinding()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        var initial = server.TimeProvider.GetUtcNow();

        var pending = await CreateDraftAsync(
            server.Client,
            token,
            "Pending filter");
        using var review = await SendJsonAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{pending.DraftId:D}/submit-review",
            token,
            Guid.NewGuid(),
            new DocumentDraftRevisionApiRequest(pending.Revision));
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);

        var firstHistory = await CreateDraftAsync(
            server.Client,
            token,
            "First history");
        using var firstDiscard = await SendJsonAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{firstHistory.DraftId:D}/discard",
            token,
            Guid.NewGuid(),
            new DocumentDraftRevisionApiRequest(firstHistory.Revision));
        Assert.Equal(HttpStatusCode.OK, firstDiscard.StatusCode);

        server.TimeProvider.MoveWallClock(TimeSpan.FromHours(1));
        var working = await CreateDraftAsync(
            server.Client,
            token,
            "Working filter");
        var secondHistory = await CreateDraftAsync(
            server.Client,
            token,
            "Second history");
        using var secondDiscard = await SendJsonAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{secondHistory.DraftId:D}/discard",
            token,
            Guid.NewGuid(),
            new DocumentDraftRevisionApiRequest(secondHistory.Revision));
        Assert.Equal(HttpStatusCode.OK, secondDiscard.StatusCode);

        using var defaultQueue = await server.Client.GetAsync("/api/drafts");
        Assert.Equal(HttpStatusCode.OK, defaultQueue.StatusCode);
        using var defaultJson = await ReadJsonAsync(defaultQueue);
        var defaultItems = defaultJson.RootElement.GetProperty("items");
        Assert.Single(defaultItems.EnumerateArray());
        Assert.Equal(
            pending.DraftId,
            defaultItems.EnumerateArray()
                .Single()
                .GetProperty("draftId")
                .GetGuid());

        var after = Uri.EscapeDataString(initial.ToString("O"));
        using var filtered = await server.Client.GetAsync(
            $"/api/drafts?queue=working&sourceActor=user_ui&updatedAfter={after}");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        using var filteredJson = await ReadJsonAsync(filtered);
        var filteredItems = filteredJson.RootElement.GetProperty("items");
        Assert.Single(filteredItems.EnumerateArray());
        Assert.Equal(
            working.DraftId,
            filteredItems.EnumerateArray()
                .Single()
                .GetProperty("draftId")
                .GetGuid());

        using var firstPage = await server.Client.GetAsync(
            "/api/drafts?queue=history&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        using var firstPageJson = await ReadJsonAsync(firstPage);
        var nextCursor = firstPageJson.RootElement
            .GetProperty("nextCursor")
            .GetString();
        Assert.NotNull(nextCursor);

        using var mismatchedCursor = await server.Client.GetAsync(
            $"/api/drafts?queue=history&status=rejected&pageSize=1&cursor={Uri.EscapeDataString(nextCursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedCursor.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidCursor,
            await ReadErrorCodeAsync(mismatchedCursor));

        using var invalidStatus = await server.Client.GetAsync(
            "/api/drafts?queue=working&status=pending_review");
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(invalidStatus));

        using var invalidPageSize = await server.Client.GetAsync(
            "/api/drafts?queue=history&pageSize=not-a-number");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPageSize.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(invalidPageSize));

        using var invalidTime = await server.Client.GetAsync(
            "/api/drafts?queue=history&updatedAfter=not-a-time");
        Assert.Equal(HttpStatusCode.BadRequest, invalidTime.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(invalidTime));
    }

    [Fact]
    public async Task DraftDetailIncludesReviewContextChangesAndBodyEdit()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        using var sourceResponse =
            await server.Client.PostJsonWithAntiforgeryAsync(
                "/api/documents",
                token,
                new CreateDocumentRequest(
                    "Source note",
                    "notes/source-note.md",
                    null,
                    VaultDocumentFormat.Markdown,
                    "# Source note\n",
                    []));
        Assert.Equal(HttpStatusCode.Created, sourceResponse.StatusCode);
        using var sourceJson = await ReadJsonAsync(sourceResponse);
        var sourceDocumentId =
            sourceJson.RootElement.GetProperty("documentId").GetGuid();

        using var targetResponse =
            await server.Client.PostJsonWithAntiforgeryAsync(
                "/api/documents",
                token,
                new CreateDocumentRequest(
                    "Review target",
                    "notes/review-target.md",
                    null,
                    VaultDocumentFormat.Markdown,
                    "---\nstatus: raw\n---\n\n# Target\n",
                    ["Redis"]));
        Assert.Equal(HttpStatusCode.Created, targetResponse.StatusCode);
        using var targetJson = await ReadJsonAsync(targetResponse);
        var targetDocumentId =
            targetJson.RootElement.GetProperty("documentId").GetGuid();
        var targetRevision =
            targetJson.RootElement.GetProperty("revision").GetInt32();

        var missingSourceId = Guid.NewGuid();
        using var payloadDocument = JsonDocument.Parse(
            $$"""
              {
                "operations": [
                  { "type": "append_body", "content": "\nReviewed\n" },
                  { "type": "set_property", "key": "status", "value": "verified" },
                  { "type": "add_tag", "tag": "Reviewed" }
                ],
                "reviewContext": {
                  "summary": "AI summary",
                  "sources": [
                    {
                      "documentId": "{{sourceDocumentId:D}}",
                      "title": "Source note",
                      "virtualPath": "notes/source-note.md",
                      "excerpt": "Source excerpt"
                    },
                    {
                      "documentId": "{{missingSourceId:D}}",
                      "title": "Missing note",
                      "excerpt": "Still submitted"
                    }
                  ]
                }
              }
              """);
        using var create = await SendJsonAsync(
            server.Client,
            HttpMethod.Post,
            "/api/drafts",
            token,
            Guid.NewGuid(),
            new CreateDocumentDraftApiRequest(
                "update",
                targetDocumentId,
                targetRevision,
                payloadDocument.RootElement.Clone()));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var created = await ReadJsonAsync(create);
        var draftId = created.RootElement.GetProperty("draftId").GetGuid();

        using var detail = await server.Client.GetAsync(
            $"/api/drafts/{draftId:D}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.True(detail.Headers.CacheControl?.NoStore);
        using var detailJson = await ReadJsonAsync(detail);
        var root = detailJson.RootElement;
        Assert.Equal(
            "AI summary",
            root.GetProperty("reviewContext")
                .GetProperty("summary")
                .GetString());
        var sources = root.GetProperty("reviewContext")
            .GetProperty("sources")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, sources.Length);
        Assert.Equal(
            sourceDocumentId,
            sources[0].GetProperty("documentId").GetGuid());
        Assert.Equal(
            JsonValueKind.Null,
            sources[1].GetProperty("documentId").ValueKind);

        var preview = root.GetProperty("preview");
        var changes = preview.GetProperty("structuredChanges");
        Assert.False(changes.GetProperty("truncated").GetBoolean());
        Assert.Contains(
            changes.GetProperty("changes").EnumerateArray(),
            change =>
                change.GetProperty("fieldKind").GetString() == "frontmatter"
                && change.GetProperty("key").GetString() == "status");
        var operations = preview.GetProperty("operationSummary");
        Assert.Equal(3, operations.GetProperty("total").GetInt32());
        Assert.Contains(
            operations.GetProperty("items").EnumerateArray(),
            item =>
                item.GetProperty("type").GetString() == "set_property"
                && item.GetProperty("count").GetInt32() == 1);
        Assert.True(
            preview.GetProperty("bodyEdit").GetProperty("canSave").GetBoolean());

        using var list = await server.Client.GetAsync(
            "/api/drafts?queue=working");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listJson = await ReadJsonAsync(list);
        var item = listJson.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .Single();
        Assert.False(item.TryGetProperty("reviewContext", out _));
        Assert.False(item.TryGetProperty("preview", out _));
    }

    private static async Task<(Guid DraftId, int Revision)> CreateDraftAsync(
        HttpClient client,
        string token,
        string title)
    {
        using var payloadDocument = JsonDocument.Parse(
            $$"""
              {
                "title": "{{title}}",
                "virtualPath": "notes/{{title.Replace(' ', '-')}}.md",
                "format": "md",
                "content": "# {{title}}\n",
                "tags": []
              }
              """);
        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/drafts",
            token,
            Guid.NewGuid(),
            new CreateDocumentDraftApiRequest(
                "create",
                null,
                null,
                payloadDocument.RootElement.Clone()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        return (
            json.RootElement.GetProperty("draftId").GetGuid(),
            json.RootElement.GetProperty("draftRevision").GetInt32());
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
