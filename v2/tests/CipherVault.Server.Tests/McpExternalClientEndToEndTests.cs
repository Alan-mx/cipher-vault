using System.Net.Http.Headers;
using System.Text.Json;
using CipherVault.Server.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CipherVault.Server.Tests;

public sealed class McpExternalClientEndToEndTests
{
    [Fact]
    public async Task OfficialClientDiscoversCurrentMcpCapabilities()
    {
        await using var server = await CreateServerAsync();
        await using var client = await CreateClientAsync(server);

        Assert.NotNull(client.ServerInfo);
        Assert.Contains(
            "Draft-only",
            client.ServerInstructions,
            StringComparison.Ordinal);

        var tools = await client.ListToolsAsync();
        Assert.Equal(
            McpToolNames.All.Order(StringComparer.Ordinal),
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));

        var prompts = await client.ListPromptsAsync();
        Assert.Equal(
            McpPromptNames.All.Order(StringComparer.Ordinal),
            prompts.Select(prompt => prompt.Name).Order(StringComparer.Ordinal));
        var prompt = await client.GetPromptAsync(
            McpPromptNames.AnswerWithKnowledge,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["question"] = "What does the Vault contain?"
            });
        var promptText = Assert.IsType<TextContentBlock>(
            Assert.Single(prompt.Messages).Content);
        Assert.Contains(
            "What does the Vault contain?",
            promptText.Text,
            StringComparison.Ordinal);

        var resources = await client.ListResourcesAsync();
        Assert.Contains(
            resources,
            resource =>
                resource.Uri
                == "cipher-vault://knowledge-model");

        var model = await client.ReadResourceAsync(
            new Uri("cipher-vault://knowledge-model"));
        var text = Assert.IsType<TextResourceContents>(
            Assert.Single(model.Contents));
        Assert.Contains("\"knowledgeTypes\"", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfficialClientCanCreateAndTraceAnApprovedSource()
    {
        await using var server = await CreateServerAsync();
        await using var client = await CreateClientAsync(server);

        var sourceDraft = await CallToolAsync(
            client,
            McpToolNames.CreateDraft,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["targetObjectType"] = "source",
                ["operationType"] = "create",
                ["payload"] = new
                {
                    sourceKind = "web_page",
                    title = "External source",
                    metadata = new
                    {
                        url = "https://example.com/external-source"
                    },
                    contentFormat = "markdown",
                    contentText = "# Evidence\n\nClaim one."
                },
                ["idempotencyKey"] = Guid.NewGuid().ToString("D")
            });
        var draftId = sourceDraft.GetProperty("draftId").GetGuid();
        var draftRevision = sourceDraft.GetProperty("draftRevision").GetInt32();

        var beforeApply = await CallToolAsync(
            client,
            McpToolNames.ListSources,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pageSize"] = 100
            });
        Assert.Empty(beforeApply.GetProperty("items").EnumerateArray());

        var sourceId = await SubmitAndApplyDraftAsync(
            server.Client,
            draftId,
            draftRevision);
        var revisions = await CallToolAsync(
            client,
            McpToolNames.ListSourceRevisions,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceId"] = sourceId,
                ["pageSize"] = 100
            });
        var revision = revisions.GetProperty("items")[0];
        Assert.Equal(1, revision.GetProperty("revision").GetInt32());

        var locators = await CallToolAsync(
            client,
            McpToolNames.ListSourceLocators,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceId"] = sourceId,
                ["revision"] = 1,
                ["pageSize"] = 100
            });
        var locator = locators.GetProperty("items")
            .EnumerateArray()
            .First(item => item.GetProperty("type").GetString() == "section");
        var locatorId = locator.GetProperty("locatorId").GetGuid();
        var locatorText = await CallToolAsync(
            client,
            McpToolNames.ReadSourceLocator,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceId"] = sourceId,
                ["revision"] = 1,
                ["locatorId"] = locatorId
            });
        Assert.Contains(
            "Claim one.",
            locatorText.GetProperty("text").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfficialClientCanSynthesizeKnowledgeFromTwoApprovedSources()
    {
        await using var server = await CreateServerAsync();
        await using var client = await CreateClientAsync(server);

        var first = await CreateSourceTraceAsync(
            server,
            client,
            "First source",
            "https://example.com/first",
            "# First claim\n\nFirst claim text.");
        var second = await CreateSourceTraceAsync(
            server,
            client,
            "Second source",
            "https://example.com/second",
            "# Second claim\n\nSecond claim text.");

        var firstKnowledgeId = await CreateKnowledgeFromSourceAsync(
            server,
            client,
            first,
            "First knowledge");
        var secondKnowledgeId = await CreateKnowledgeFromSourceAsync(
            server,
            client,
            second,
            "Second knowledge");

        var model = await CallToolAsync(
            client,
            McpToolNames.GetKnowledgeModel,
            new Dictionary<string, object?>());
        var knowledgeTypeId = model.GetProperty("knowledgeTypes")
            .EnumerateArray()
            .First(item => item.GetProperty("isEnabled").GetBoolean())
            .GetProperty("typeId")
            .GetString();

        var synthesisDraft = await CallToolAsync(
            client,
            McpToolNames.CreateDraft,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["targetObjectType"] = "knowledge",
                ["operationType"] = "create",
                ["payload"] = JsonSerializer.SerializeToElement(new
                {
                    title = "Synthesized knowledge",
                    summary = "A synthesis of two reviewed sources.",
                    knowledgeTypeId,
                    templateId = (string?)null,
                    contentMarkdown =
                        "Both reviewed sources contain a claim.",
                    properties = new { },
                    tags = new[] { "synthesis" },
                    sourceReferences = new[]
                    {
                        new
                        {
                            sourceId = first.SourceId,
                            sourceRevision = first.Revision,
                            locatorType = "section",
                            locatorValue = first.LocatorValue,
                            excerpt = first.Excerpt,
                            extractionMetadata = new
                            {
                                method = "mcp_external_client_e2e"
                            }
                        },
                        new
                        {
                            sourceId = second.SourceId,
                            sourceRevision = second.Revision,
                            locatorType = "section",
                            locatorValue = second.LocatorValue,
                            excerpt = second.Excerpt,
                            extractionMetadata = new
                            {
                                method = "mcp_external_client_e2e"
                            }
                        }
                    }
                }),
                ["idempotencyKey"] = Guid.NewGuid().ToString("D")
            });
        var synthesisId = await SubmitAndApplyDraftAsync(
            server.Client,
            synthesisDraft.GetProperty("draftId").GetGuid(),
            synthesisDraft.GetProperty("draftRevision").GetInt32());

        var synthesized = await CallToolAsync(
            client,
            McpToolNames.ReadKnowledge,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["knowledgeId"] = synthesisId
            });
        var detail = synthesized.GetProperty("knowledge");
        Assert.Equal("Synthesized knowledge", detail.GetProperty("title").GetString());
        Assert.Equal(
            new[] { first.SourceId, second.SourceId }.Order(),
            detail.GetProperty("sourceReferences")
                .EnumerateArray()
                .Select(item => item.GetProperty("sourceId").GetGuid())
                .Order()
                .ToArray());

        var search = await CallToolAsync(
            client,
            McpToolNames.SearchKnowledge,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["query"] = "Synthesized",
                ["mode"] = "fulltext",
                ["pageSize"] = 20
            });
        Assert.Contains(
            search.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("knowledgeId").GetGuid() == synthesisId);
    }

    private static async Task<ServerTestApplication> CreateServerAsync()
    {
        var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        unlock.EnsureSuccessStatusCode();
        return server;
    }

    private static async Task<McpClient> CreateClientAsync(
        ServerTestApplication server)
    {
        var token = await McpTestClient.CreateTokenAsync(server.Client);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Client.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["Authorization"] = $"Bearer {token}"
            }
        });
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<JsonElement> CallToolAsync(
        McpClient client,
        string name,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(name, arguments);
        Assert.False(result.IsError);
        if (result.StructuredContent is JsonElement structured)
        {
            return structured.Clone();
        }

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var json = JsonDocument.Parse(text.Text);
        return json.RootElement.Clone();
    }

    private static async Task<Guid> SubmitAndApplyDraftAsync(
        HttpClient client,
        Guid draftId,
        int draftRevision)
    {
        var csrf = await client.GetAntiforgeryTokenAsync();
        using var submitted = await client.PostJsonWithAntiforgeryAsync(
            $"/api/drafts/{draftId:D}/submit-review",
            csrf,
            new { expectedDraftRevision = draftRevision },
            Guid.NewGuid());
        var submittedBody = await submitted.Content.ReadAsStringAsync();
        Assert.True(submitted.IsSuccessStatusCode, submittedBody);
        using var submittedJson = JsonDocument.Parse(submittedBody);
        var submittedRevision = submittedJson.RootElement
            .GetProperty("draftRevision")
            .GetInt32();

        using var applied = await client.PostJsonWithAntiforgeryAsync(
            $"/api/drafts/{draftId:D}/apply",
            csrf,
            new { expectedDraftRevision = submittedRevision },
            Guid.NewGuid());
        var appliedBody = await applied.Content.ReadAsStringAsync();
        Assert.True(applied.IsSuccessStatusCode, appliedBody);
        using var appliedJson = JsonDocument.Parse(appliedBody);
        return appliedJson.RootElement.GetProperty("objectId").GetGuid();
    }

    private static async Task<SourceTrace> CreateSourceTraceAsync(
        ServerTestApplication server,
        McpClient client,
        string title,
        string url,
        string content)
    {
        var sourceDraft = await CallToolAsync(
            client,
            McpToolNames.CreateDraft,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["targetObjectType"] = "source",
                ["operationType"] = "create",
                ["payload"] = new
                {
                    sourceKind = "web_page",
                    title,
                    metadata = new { url },
                    contentFormat = "markdown",
                    contentText = content
                },
                ["idempotencyKey"] = Guid.NewGuid().ToString("D")
            });
        var sourceId = await SubmitAndApplyDraftAsync(
            server.Client,
            sourceDraft.GetProperty("draftId").GetGuid(),
            sourceDraft.GetProperty("draftRevision").GetInt32());

        var locators = await CallToolAsync(
            client,
            McpToolNames.ListSourceLocators,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceId"] = sourceId,
                ["revision"] = 1,
                ["pageSize"] = 100
            });
        var locator = locators.GetProperty("items")
            .EnumerateArray()
            .First(item => item.GetProperty("type").GetString() == "section");
        var locatorText = await CallToolAsync(
            client,
            McpToolNames.ReadSourceLocator,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceId"] = sourceId,
                ["revision"] = 1,
                ["locatorId"] = locator.GetProperty("locatorId").GetGuid()
            });
        var text = locatorText.GetProperty("text").GetString() ?? string.Empty;
        return new SourceTrace(
            sourceId,
            1,
            locator.GetProperty("value").Clone(),
            text.Trim(),
            text);
    }

    private static async Task<Guid> CreateKnowledgeFromSourceAsync(
        ServerTestApplication server,
        McpClient client,
        SourceTrace source,
        string title)
    {
        var model = await CallToolAsync(
            client,
            McpToolNames.GetKnowledgeModel,
            new Dictionary<string, object?>());
        var knowledgeTypeId = model.GetProperty("knowledgeTypes")
            .EnumerateArray()
            .First(item => item.GetProperty("isEnabled").GetBoolean())
            .GetProperty("typeId")
            .GetString();

        var knowledgeDraft = await CallToolAsync(
            client,
            McpToolNames.CreateDraft,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["targetObjectType"] = "knowledge",
                ["operationType"] = "create",
                ["payload"] = JsonSerializer.SerializeToElement(new
                {
                    title,
                    summary = $"Extracted from {source.SourceId:D}.",
                    knowledgeTypeId,
                    templateId = (string?)null,
                    contentMarkdown = source.Excerpt,
                    properties = new { },
                    tags = new[] { "external" },
                    sourceReferences = new[]
                    {
                        new
                        {
                            sourceId = source.SourceId,
                            sourceRevision = source.Revision,
                            locatorType = "section",
                            locatorValue = source.LocatorValue,
                            excerpt = source.Excerpt,
                            extractionMetadata = new
                            {
                                method = "mcp_external_client_e2e"
                            }
                        }
                    }
                }),
                ["idempotencyKey"] = Guid.NewGuid().ToString("D")
            });
        return await SubmitAndApplyDraftAsync(
            server.Client,
            knowledgeDraft.GetProperty("draftId").GetGuid(),
            knowledgeDraft.GetProperty("draftRevision").GetInt32());
    }

    private sealed record SourceTrace(
        Guid SourceId,
        int Revision,
        JsonElement LocatorValue,
        string Excerpt,
        string LocatorText);
}
