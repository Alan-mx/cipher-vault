using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Server.Api;

namespace CipherVault.Server.Tests;

public sealed class RelationSuggestionApiTests
{
    [Fact]
    public async Task ApplyListAndRetractUseUserUiAuditContract()
    {
        await using var server = await CreateServerAsync();
        var token = await McpTestClient.CreateTokenAsync(server.Client);
        var source = await McpTestClient.CreateDocumentAsync(
            server.Client,
            "Source",
            "source.md");
        var target = await McpTestClient.CreateDocumentAsync(
            server.Client,
            "Target",
            "target.md");
        var suggestion = await CreateSuggestionAsync(
            server.Client,
            token,
            source,
            target);
        var suggestionId = suggestion
            .GetProperty("metadata")
            .GetProperty("suggestionId")
            .GetGuid();

        using var summary = await server.Client.GetAsync(
            "/api/relation-suggestions/summary");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.True(summary.Headers.CacheControl?.NoStore);
        using var summaryJson = await ReadJsonAsync(summary);
        Assert.Equal(
            1,
            summaryJson.RootElement
                .GetProperty("pendingReview")
                .GetInt32());

        using var list = await server.Client.GetAsync(
            "/api/relation-suggestions");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listJson = await ReadJsonAsync(list);
        Assert.Single(listJson.RootElement.GetProperty("items").EnumerateArray());

        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        using var apply = await PostWithCsrfAndKeyAsync(
            server.Client,
            $"/api/relation-suggestions/{suggestionId:D}/apply",
            csrf,
            Guid.NewGuid(),
            new { expectedSuggestionRevision = 1 });
        var applyBody = await apply.Content.ReadAsStringAsync();
        Assert.True(apply.IsSuccessStatusCode, applyBody);
        using var applyJson = JsonDocument.Parse(applyBody);
        Assert.Equal(
            "applied",
            applyJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "inserted",
            applyJson.RootElement.GetProperty("applyResult").GetString());
        var relationId = applyJson.RootElement
            .GetProperty("relationId")
            .GetGuid();

        using var relations = await server.Client.GetAsync(
            $"/api/documents/{source.DocumentId:D}/relations");
        using var relationsJson = await ReadJsonAsync(relations);
        var relation = Assert.Single(
            relationsJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(relationId, relation.GetProperty("relationId").GetGuid());
        Assert.Equal("outgoing", relation.GetProperty("direction").GetString());

        using var retract = await PostWithCsrfAndKeyAsync(
            server.Client,
            $"/api/document-relations/{relationId:D}/retract",
            csrf,
            Guid.NewGuid(),
            new { });
        var retractBody = await retract.Content.ReadAsStringAsync();
        Assert.True(retract.IsSuccessStatusCode, retractBody);
        using var retractJson = JsonDocument.Parse(retractBody);
        Assert.Equal(
            "retracted",
            retractJson.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task StaleApplyPersistsOutcomeAndRetryReturnsSameConflict()
    {
        await using var server = await CreateServerAsync();
        var token = await McpTestClient.CreateTokenAsync(server.Client);
        var source = await McpTestClient.CreateDocumentAsync(
            server.Client,
            "Source",
            "source.md");
        var target = await McpTestClient.CreateDocumentAsync(
            server.Client,
            "Target",
            "target.md");
        var suggestion = await CreateSuggestionAsync(
            server.Client,
            token,
            source,
            target);
        var suggestionId = suggestion
            .GetProperty("metadata")
            .GetProperty("suggestionId")
            .GetGuid();
        _ = await server.Manager.ExecuteAsync<int>(
            async (context, _) =>
            {
                await context.BeginTransactionAsync();
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    UPDATE documents
                    SET revision = revision + 1
                    WHERE document_id = $documentId;
                    """;
                command.Command.Parameters.AddWithValue(
                    "$documentId",
                    target.DocumentId.ToString("D"));
                await command.Command.ExecuteNonQueryAsync();
                await context.CommitTransactionAsync();
                return 1;
            });
        var csrf = await server.Client.GetAntiforgeryTokenAsync();

        var key = Guid.NewGuid();
        using var first = await PostWithCsrfAndKeyAsync(
            server.Client,
            $"/api/relation-suggestions/{suggestionId:D}/apply",
            csrf,
            key,
            new { expectedSuggestionRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        using var firstJson = await ReadJsonAsync(first);
        Assert.Equal(
            ApiErrorCodes.RelationSuggestionStale,
            firstJson.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "stale",
            firstJson.RootElement
                .GetProperty("details")
                .GetProperty("status")
                .GetString());

        using var second = await PostWithCsrfAndKeyAsync(
            server.Client,
            $"/api/relation-suggestions/{suggestionId:D}/apply",
            csrf,
            key,
            new { expectedSuggestionRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var secondJson = await ReadJsonAsync(second);
        Assert.Equal(
            ApiErrorCodes.RelationSuggestionStale,
            secondJson.RootElement.GetProperty("code").GetString());

        using var detail = await server.Client.GetAsync(
            $"/api/relation-suggestions/{suggestionId:D}");
        using var detailJson = await ReadJsonAsync(detail);
        Assert.Equal(
            "stale",
            detailJson.RootElement
                .GetProperty("metadata")
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    public async Task WritesRequireCsrfAndUuidIdempotencyKey()
    {
        await using var server = await CreateServerAsync();
        var token = await McpTestClient.CreateTokenAsync(server.Client);
        var source = await McpTestClient.CreateDocumentAsync(
            server.Client,
            "Source",
            "source.md");
        var target = await McpTestClient.CreateDocumentAsync(
            server.Client,
            "Target",
            "target.md");
        var suggestion = await CreateSuggestionAsync(
            server.Client,
            token,
            source,
            target);
        var suggestionId = suggestion
            .GetProperty("metadata")
            .GetProperty("suggestionId")
            .GetGuid();

        using var noCsrf = await server.Client.PostAsJsonAsync(
            $"/api/relation-suggestions/{suggestionId:D}/reject",
            new { expectedSuggestionRevision = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Equal(
            ApiErrorCodes.AntiforgeryInvalid,
            await ReadErrorCodeAsync(noCsrf));

        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        using var noKey = await server.Client.PostJsonWithAntiforgeryAsync(
            $"/api/relation-suggestions/{suggestionId:D}/reject",
            csrf,
            new { expectedSuggestionRevision = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(
            ApiErrorCodes.IdempotencyKeyRequired,
            await ReadErrorCodeAsync(noKey));

        using var rejected = await PostWithCsrfAndKeyAsync(
            server.Client,
            $"/api/relation-suggestions/{suggestionId:D}/reject",
            csrf,
            Guid.NewGuid(),
            new { expectedSuggestionRevision = 1 });
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.True(rejected.IsSuccessStatusCode, rejectedBody);
        using var rejectedJson = JsonDocument.Parse(rejectedBody);
        Assert.Equal(
            "rejected",
            rejectedJson.RootElement.GetProperty("status").GetString());

        using var invalidFilter = await server.Client.GetAsync(
            "/api/relation-suggestions?sourceDocumentId=not-a-uuid");
        Assert.Equal(HttpStatusCode.BadRequest, invalidFilter.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(invalidFilter));

        using var invalidPage = await server.Client.GetAsync(
            "/api/relation-suggestions?pageSize=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(invalidPage));
    }

    private static async Task<ServerTestApplication> CreateServerAsync()
    {
        var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        return server;
    }

    private static async Task<JsonElement> CreateSuggestionAsync(
        HttpClient client,
        string token,
        (Guid DocumentId, int Revision) source,
        (Guid DocumentId, int Revision) target) =>
        await McpTestClient.CallToolAsync(
            client,
            token,
            "create_relation_suggestion",
            new
            {
                relationType = "depends_on",
                sourceDocumentId = source.DocumentId.ToString("D"),
                sourceDocumentRevision = source.Revision,
                targetDocumentId = target.DocumentId.ToString("D"),
                targetDocumentRevision = target.Revision,
                reason = "Source depends on target.",
                modelName = "test-model",
                evidence = Array.Empty<object>(),
                idempotencyKey = Guid.NewGuid().ToString("D")
            });

    private static async Task<HttpResponseMessage> PostWithCsrfAndKeyAsync<T>(
        HttpClient client,
        string path,
        string csrf,
        Guid key,
        T value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(value)
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await client.SendAsync(request);
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
