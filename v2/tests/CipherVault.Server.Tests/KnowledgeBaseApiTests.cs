using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class KnowledgeBaseApiTests
{
    [Fact]
    public async Task SourceDraftCanBeSubmittedAppliedAndRead()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var csrf = await server.Client.GetAntiforgeryTokenAsync();

        using var create = await SendAsync(
            server.Client,
            HttpMethod.Post,
            "/api/drafts",
            csrf,
            new
            {
                targetObjectType = "source",
                operationType = "create",
                targetId = (string?)null,
                baseRevision = (int?)null,
                payload = new
                {
                    sourceKind = "web_page",
                    title = "Source draft",
                    metadata = new { url = "https://example.com/article" },
                    contentFormat = "markdown",
                    contentText = "# Section\n\nParagraph text."
                }
            });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(
            await create.Content.ReadAsStringAsync());
        var draftId = created.RootElement.GetProperty("draftId").GetString();
        Assert.NotNull(draftId);
        await Task.Delay(100);

        using var submit = await SendAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{draftId}/submit-review",
            csrf,
            new { expectedDraftRevision = 1 });
        var submitBody = await submit.Content.ReadAsStringAsync();
        Assert.True(
            submit.StatusCode == HttpStatusCode.OK,
            $"Submit failed: {(int)submit.StatusCode} {submitBody}");

        using var apply = await SendAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{draftId}/apply",
            csrf,
            new { expectedDraftRevision = 2 });
        var applyBody = await apply.Content.ReadAsStringAsync();
        Assert.True(
            apply.StatusCode == HttpStatusCode.OK,
            $"Apply failed: {(int)apply.StatusCode} {applyBody}");
        using var applied = JsonDocument.Parse(applyBody);
        Assert.Equal("source", applied.RootElement.GetProperty("objectType").GetString());
        var sourceId = applied.RootElement.GetProperty("objectId").GetString();
        Assert.NotNull(sourceId);

        using var list = await server.Client.GetAsync("/api/sources");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var sources = JsonDocument.Parse(
            await list.Content.ReadAsStringAsync());
        Assert.Single(sources.RootElement.GetProperty("items").EnumerateArray());

        using var content = await server.Client.GetAsync(
            $"/api/sources/{sourceId}/revisions/1/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        using var chunk = JsonDocument.Parse(
            await content.Content.ReadAsStringAsync());
        Assert.Contains(
            "Paragraph text.",
            chunk.RootElement.GetProperty("text").GetString());

        using var createKnowledge = await SendAsync(
            server.Client,
            HttpMethod.Post,
            "/api/drafts",
            csrf,
            new
            {
                targetObjectType = "knowledge",
                operationType = "create",
                targetId = (string?)null,
                baseRevision = (int?)null,
                payload = new
                {
                    title = "Knowledge from source",
                    summary = "Summary",
                    knowledgeTypeId = "00000000-0000-4000-8000-000000001001",
                    templateId = (string?)null,
                    contentMarkdown = "# Knowledge from source",
                    properties = new { },
                    tags = new[] { "demo" },
                    sourceReferences = new[]
                    {
                        new
                        {
                            sourceId,
                            sourceRevision = 1,
                            locatorType = "section",
                            locatorValue = new[] { "Section" },
                            excerpt = "Paragraph text.",
                            extractionMetadata = new { method = "test" }
                        }
                    }
                }
            });
        Assert.Equal(HttpStatusCode.Created, createKnowledge.StatusCode);
        using var knowledgeDraft = JsonDocument.Parse(
            await createKnowledge.Content.ReadAsStringAsync());
        var knowledgeDraftId = knowledgeDraft.RootElement
            .GetProperty("draftId")
            .GetString();

        using var submitKnowledge = await SendAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{knowledgeDraftId}/submit-review",
            csrf,
            new { expectedDraftRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, submitKnowledge.StatusCode);
        using var applyKnowledge = await SendAsync(
            server.Client,
            HttpMethod.Post,
            $"/api/drafts/{knowledgeDraftId}/apply",
            csrf,
            new { expectedDraftRevision = 2 });
        var applyKnowledgeBody = await applyKnowledge.Content.ReadAsStringAsync();
        Assert.True(
            applyKnowledge.StatusCode == HttpStatusCode.OK,
            $"Knowledge apply failed: {(int)applyKnowledge.StatusCode} {applyKnowledgeBody}");
        using var appliedKnowledge = JsonDocument.Parse(applyKnowledgeBody);
        Assert.Equal(
            "knowledge",
            appliedKnowledge.RootElement.GetProperty("objectType").GetString());

        using var search = await server.Client.PostAsJsonAsync(
            "/api/knowledge/search",
            new { query = "Knowledge", mode = "fulltext", pageSize = 20 });
        var searchBody = await search.Content.ReadAsStringAsync();
        Assert.True(
            search.StatusCode == HttpStatusCode.OK,
            $"Search failed: {(int)search.StatusCode} {searchBody}");
        using var searchResult = JsonDocument.Parse(
            await search.Content.ReadAsStringAsync());
        Assert.Single(
            searchResult.RootElement.GetProperty("items").EnumerateArray());
    }

    private static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client,
        HttpMethod method,
        string path,
        string csrf,
        T body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await client.SendAsync(request);
    }
}
