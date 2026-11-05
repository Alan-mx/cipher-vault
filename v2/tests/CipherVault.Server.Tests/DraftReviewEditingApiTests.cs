using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class DraftReviewEditingApiTests
{
    [Fact]
    public async Task GetReturnsChangesAndValidationContract()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        var draft = await CreateSourceDraftAsync(
            server.Client,
            csrf,
            "Preview");

        using var response = await server.Client.GetAsync(
            $"/api/drafts/{draft.DraftId:D}");
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var preview = json.RootElement.GetProperty("preview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(preview.GetProperty("changes").GetArrayLength() > 0);
        Assert.False(preview.GetProperty("changesTruncated").GetBoolean());
        Assert.Equal(
            JsonValueKind.Array,
            preview.GetProperty("validationIssues").ValueKind);
        Assert.Contains(
            preview.GetProperty("changes").EnumerateArray(),
            change =>
                change.GetProperty("fieldPath").GetString() == "sourceKind"
                && change.GetProperty("kind").GetString() == "new");
    }

    [Fact]
    public async Task UserUiCanEditPendingReviewAndRevisionConflictIsRejected()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        var draft = await CreateSourceDraftAsync(
            server.Client,
            csrf,
            "Pending");
        using var submitted =
            await server.Client.PostJsonWithAntiforgeryAsync(
                $"/api/drafts/{draft.DraftId:D}/submit-review",
                csrf,
                new { expectedDraftRevision = 1 },
                Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var updated = await PutAsync(
            server.Client,
            draft.DraftId,
            2,
            SourcePayload("Edited pending"),
            csrf,
            Guid.NewGuid());
        using var updatedJson = JsonDocument.Parse(
            await updated.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(
            "draft",
            updatedJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, updatedJson.RootElement.GetProperty("draftRevision").GetInt32());

        using var stale = await PutAsync(
            server.Client,
            draft.DraftId,
            2,
            SourcePayload("Stale"),
            csrf,
            Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains(
            "draft_revision_conflict",
            await stale.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpBearerCannotUseWebDraftUpdate()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        var draft = await CreateSourceDraftAsync(
            server.Client,
            csrf,
            "MCP block");
        var token = await McpTestClient.CreateTokenAsync(server.Client);
        using var mcpClient = server.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/drafts/{draft.DraftId:D}")
        {
            Content = JsonContent.Create(new
            {
                expectedDraftRevision = 1,
                payload = SourcePayload("MCP edit")
            })
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        using var response = await mcpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("forbidden", body, StringComparison.Ordinal);
        Assert.DoesNotContain("antiforgery", body, StringComparison.Ordinal);
    }

    private static async Task<(Guid DraftId, int DraftRevision)>
        CreateSourceDraftAsync(
            HttpClient client,
            string csrf,
            string title)
    {
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/api/drafts",
            csrf,
            new
            {
                targetObjectType = "source",
                operationType = "create",
                targetId = (string?)null,
                baseRevision = (int?)null,
                payload = SourcePayload(title)
            },
            Guid.NewGuid());
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return (
            json.RootElement.GetProperty("draftId").GetGuid(),
            json.RootElement.GetProperty("draftRevision").GetInt32());
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        Guid draftId,
        int expectedRevision,
        object payload,
        string csrf,
        Guid idempotencyKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/drafts/{draftId:D}")
        {
            Content = JsonContent.Create(new
            {
                expectedDraftRevision = expectedRevision,
                payload
            })
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Headers.Add(
            "Idempotency-Key",
            idempotencyKey.ToString("D"));
        return client.SendAsync(request);
    }

    private static object SourcePayload(string title) =>
        new
        {
            sourceKind = "web_page",
            title,
            metadata = new { url = "https://example.com" },
            contentFormat = "markdown",
            contentText = "# " + title
        };
}
