using System.Net;

namespace CipherVault.Server.Tests;

public sealed class McpRelationSuggestionToolsTests
{
    [Fact]
    public async Task UnsupportedEvidenceFieldReturnsInvalidInput()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await McpTestClient.CreateTokenAsync(server.Client);
        var source = await McpTestClient.CreateDocumentAsync(
            server.Client,
            "Source",
            "source.md");
        var target = await McpTestClient.CreateDocumentAsync(
            server.Client,
            "Target",
            "target.md");

        var result = await McpTestClient.CallToolAsync(
            server.Client,
            token,
            "create_relation_suggestion",
            new
            {
                relationType = "depends_on",
                sourceDocumentId = source.DocumentId.ToString("D"),
                sourceDocumentRevision = source.Revision,
                targetDocumentId = target.DocumentId.ToString("D"),
                targetDocumentRevision = target.Revision,
                reason = "Unsupported evidence.",
                modelName = "test-model",
                evidence = new[]
                {
                    new
                    {
                        documentId = source.DocumentId.ToString("D"),
                        documentRevision = source.Revision,
                        headingPath = new[] { "Source" },
                        excerpt = "Evidence",
                        url = "https://example.invalid"
                    }
                },
                idempotencyKey = Guid.NewGuid().ToString("D")
            });

        Assert.Equal("invalid_input", result.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RelationToolsCreateReadUpdateAndDiscardWithoutReviewTools()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await McpTestClient.CreateTokenAsync(server.Client);
        var source = await McpTestClient.CreateDocumentAsync(
            server.Client,
            "Source",
            "source.md");
        var target = await McpTestClient.CreateDocumentAsync(
            server.Client,
            "Target",
            "target.md");
        var createKey = Guid.NewGuid().ToString("D");
        var createArguments = new
        {
            relationType = "depends_on",
            sourceDocumentId = source.DocumentId.ToString("D"),
            sourceDocumentRevision = source.Revision,
            targetDocumentId = target.DocumentId.ToString("D"),
            targetDocumentRevision = target.Revision,
            reason = "Source depends on target.",
            modelName = "test-model",
            evidence = new[]
            {
                new
                {
                    documentId = source.DocumentId.ToString("D"),
                    documentRevision = source.Revision,
                    headingPath = new[] { "Source" },
                    excerpt = "Evidence"
                }
            },
            idempotencyKey = createKey
        };

        var created = await McpTestClient.CallToolAsync(
            server.Client,
            token,
            "create_relation_suggestion",
            createArguments);
        var suggestionId = created
            .GetProperty("metadata")
            .GetProperty("suggestionId")
            .GetGuid();
        var replay = await McpTestClient.CallToolAsync(
            server.Client,
            token,
            "create_relation_suggestion",
            createArguments);
        Assert.Equal(
            suggestionId,
            replay.GetProperty("metadata")
                .GetProperty("suggestionId")
                .GetGuid());

        var list = await McpTestClient.CallToolAsync(
            server.Client,
            token,
            "list_relation_suggestions",
            new { });
        Assert.Contains(
            list.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("suggestionId").GetGuid() ==
                suggestionId);

        var read = await McpTestClient.CallToolAsync(
            server.Client,
            token,
            "read_relation_suggestion",
            new { suggestionId = suggestionId.ToString("D") });
        Assert.Equal(
            "test-model",
            read.GetProperty("modelName").GetString());

        var updated = await McpTestClient.CallToolAsync(
            server.Client,
            token,
            "update_relation_suggestion",
            new
            {
                suggestionId = suggestionId.ToString("D"),
                expectedSuggestionRevision = 1,
                reason = "Updated reason.",
                modelName = "test-model-2",
                evidence = Array.Empty<object>(),
                idempotencyKey = Guid.NewGuid().ToString("D")
            });
        Assert.Equal(
            2,
            updated.GetProperty("metadata")
                .GetProperty("suggestionRevision")
                .GetInt32());

        _ = await McpTestClient.CallToolAsync(
            server.Client,
            token,
            "discard_relation_suggestion",
            new
            {
                suggestionId = suggestionId.ToString("D"),
                expectedSuggestionRevision = 2,
                idempotencyKey = Guid.NewGuid().ToString("D")
            });
        var listAfter = await McpTestClient.CallToolAsync(
            server.Client,
            token,
            "list_relation_suggestions",
            new { });
        Assert.Empty(listAfter.GetProperty("items").EnumerateArray());

        var tools = await McpTestClient.ListToolsAsync(server.Client, token);
        var names = tools.GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("create_relation_suggestion", names);
        Assert.Contains("list_relation_suggestions", names);
        Assert.Contains("read_relation_suggestion", names);
        Assert.Contains("update_relation_suggestion", names);
        Assert.Contains("discard_relation_suggestion", names);
        Assert.DoesNotContain("apply_relation_suggestion", names);
        Assert.DoesNotContain("reject_relation_suggestion", names);
        Assert.DoesNotContain("retract_document_relation", names);
    }
}
