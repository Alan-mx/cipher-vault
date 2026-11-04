using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CipherVault.Server.Tests;

public sealed class McpToolsIntegrationTests
{
    [Fact]
    public async Task FifteenToolsCoverReadSearchKnowledgeAndWritePaths()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await CreateTokenAsync(server.Client);
        await server.Client.SetMcpWriteModeAsync("direct");

        var created = await CallToolAsync(server.Client, token, "create_note", new
        {
            title = "MCP Center",
            content = "---\ntags: [Work]\n---\n# MCP Center\nCenter body",
            folder = "Mcp",
            format = "md",
            tags = new[] { "Work" }
        });
        var centerId = created.GetProperty("documentId").GetGuid();

        var linked = await CallToolAsync(server.Client, token, "create_note", new
        {
            title = "Linked",
            content = "# Linked\nSee [[MCP Center]]",
            format = "md"
        });
        Assert.Equal(1, linked.GetProperty("revision").GetInt32());

        var catalog = await CallToolAsync(server.Client, token, "list_notes", new
        {
            folder = "Mcp",
            format = "md",
            pageSize = 10
        });
        Assert.Contains("MCP Center", catalog.GetProperty("items").GetRawText(), StringComparison.Ordinal);

        var scan = await CallToolAsync(server.Client, token, "scan_notes", new
        {
            pageSize = 10
        });
        Assert.Equal(2, scan.GetProperty("items").GetArrayLength());

        var read = await CallToolAsync(server.Client, token, "read_note", new
        {
            documentId = centerId,
            offset = 0,
            maxChars = 100
        });
        Assert.Equal(
            0,
            read.GetProperty("offset").GetInt32());
        Assert.True(read.GetProperty("totalLength").GetInt32() > 0);

        var search = await CallToolAsync(server.Client, token, "search_notes", new
        {
            query = "Center",
            tags = new[] { "Work" },
            format = "md"
        });
        Assert.Single(search.GetProperty("hits").EnumerateArray());

        var invalidSearchFilters = await CallToolAsync(
            server.Client,
            token,
            "search_notes",
            new
            {
                query = "Center",
                hasBacklinks = true,
                orphanOnly = true
            });
        Assert.Equal(
            "invalid_input",
            invalidSearchFilters.GetProperty("code").GetString());

        var tags = await CallToolAsync(server.Client, token, "list_tags", new
        {
            pageSize = 10
        });
        Assert.Contains("Work", tags.GetProperty("items").GetRawText(), StringComparison.Ordinal);

        var map = await CallToolAsync(server.Client, token, "get_document_map", new
        {
            documentId = centerId
        });
        Assert.Equal(
            centerId,
            map.GetProperty("map").GetProperty("metadata").GetProperty("documentId").GetGuid());

        var linkedId = scan.GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("title").GetString() == "Linked")
            .GetProperty("documentId")
            .GetGuid();
        var backlinks = await CallToolAsync(server.Client, token, "get_backlinks", new
        {
            documentId = centerId
        });
        Assert.Single(backlinks.GetProperty("items").EnumerateArray());

        var mentions = await CallToolAsync(server.Client, token, "find_unlinked_mentions", new
        {
            documentId = centerId
        });
        Assert.Equal(
            centerId,
            mentions.GetProperty("targetDocumentId").GetGuid());

        var graph = await CallToolAsync(server.Client, token, "get_local_graph", new
        {
            documentId = centerId,
            depth = 1
        });
        Assert.True(graph.GetProperty("nodes").GetArrayLength() >= 1);

        var appended = await CallToolAsync(server.Client, token, "append_note", new
        {
            documentId = centerId,
            expectedRevision = 1,
            content = "\nAppended body"
        });
        Assert.Equal(2, appended.GetProperty("revision").GetInt32());

        var updated = await CallToolAsync(server.Client, token, "update_note", new
        {
            documentId = centerId,
            expectedRevision = 2,
            changes = new
            {
                tags = new[] { "New" }
            }
        });
        Assert.Equal(3, updated.GetProperty("revision").GetInt32());

        var patched = await CallToolAsync(server.Client, token, "patch_note", new
        {
            documentId = centerId,
            expectedRevision = 3,
            targetKind = "heading",
            operation = "append",
            headingPath = new[] { "MCP Center" },
            content = "Patched body"
        });
        Assert.True(patched.GetProperty("changed").GetBoolean());

        _ = await server.Manager.LockAsync();
        using var lockedResponse = await PostToolAsync(
            server.Client,
            token,
            "delete_note",
            new
            {
                documentId = linkedId,
                expectedRevision = 1
            });
        var lockedBody = await lockedResponse.Content.ReadAsStringAsync();
        Assert.True(lockedResponse.IsSuccessStatusCode, lockedBody);
        Assert.Contains("vault_locked", lockedBody, StringComparison.Ordinal);
        Assert.Contains("\"isError\":true", lockedBody, StringComparison.Ordinal);

        using var unlockAgain = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlockAgain.StatusCode);
        await server.Client.SetMcpWriteModeAsync("direct");
        var deleted = await CallToolAsync(server.Client, token, "delete_note", new
        {
            documentId = linkedId,
            expectedRevision = 1
        });
        Assert.Equal(2, deleted.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task DocumentMapReturnsMultipleResolvedOutgoingLinks()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await CreateTokenAsync(server.Client);
        await server.Client.SetMcpWriteModeAsync("direct");
        _ = await CallToolAsync(server.Client, token, "create_note", new
        {
            title = "First Target",
            content = "# First Target",
            format = "md"
        });
        _ = await CallToolAsync(server.Client, token, "create_note", new
        {
            title = "Second Target",
            content = "# Second Target",
            format = "md"
        });
        var source = await CallToolAsync(server.Client, token, "create_note", new
        {
            title = "Map Source",
            content = "# Map Source\n\n[[First Target]]\n\n[[Second Target]]",
            format = "md"
        });

        var map = await CallToolAsync(server.Client, token, "get_document_map", new
        {
            documentId = source.GetProperty("documentId").GetGuid()
        });

        Assert.Equal(
            2,
            map.GetProperty("map")
                .GetProperty("outgoingLinks")
                .GetArrayLength());
    }

    [Fact]
    public async Task ToolErrorsAndPresenceSemanticsAreStable()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await CreateTokenAsync(server.Client);
        await server.Client.SetMcpWriteModeAsync("direct");
        var created = await CallToolAsync(server.Client, token, "create_note", new
        {
            title = "Errors",
            content = "\U0001F600 body",
            format = "txt"
        });
        var documentId = created.GetProperty("documentId").GetGuid();

        var notFound = await CallToolAsync(server.Client, token, "read_note", new
        {
            documentId = Guid.NewGuid(),
            offset = 0,
            maxChars = 100
        });
        Assert.Equal("not_found", notFound.GetProperty("code").GetString());

        var revisionConflict = await CallToolAsync(
            server.Client,
            token,
            "append_note",
            new
            {
                documentId,
                expectedRevision = 2,
                content = "stale"
            });
        Assert.Equal(
            "document_revision_conflict",
            revisionConflict.GetProperty("code").GetString());
        Assert.Equal(
            1,
            revisionConflict
                .GetProperty("details")
                .GetProperty("currentRevision")
                .GetInt32());

        var nullCreateContent = await CallToolAsync(
            server.Client,
            token,
            "create_note",
            new
            {
                title = "Null Content",
                content = (string?)null,
                format = "txt"
            });
        Assert.Equal(
            "invalid_input",
            nullCreateContent.GetProperty("code").GetString());

        var nullAppendContent = await CallToolAsync(
            server.Client,
            token,
            "append_note",
            new
            {
                documentId,
                expectedRevision = 1,
                content = (string?)null
            });
        Assert.Equal(
            "invalid_input",
            nullAppendContent.GetProperty("code").GetString());

        var invalidOffset = await CallToolAsync(server.Client, token, "read_note", new
        {
            documentId,
            offset = 1,
            maxChars = 100
        });
        Assert.True(invalidOffset.TryGetProperty("_error", out _));
        Assert.Equal(
            "invalid_offset",
            invalidOffset.GetProperty("code").GetString());

        var explicitNull = await CallToolAsync(server.Client, token, "update_note", new
        {
            documentId,
            expectedRevision = 1,
            changes = new
            {
                title = (string?)null
            }
        });
        Assert.True(explicitNull.TryGetProperty("_error", out _));
        Assert.Equal("invalid_input", explicitNull.GetProperty("code").GetString());

        var tooLarge = await CallToolAsync(server.Client, token, "append_note", new
        {
            documentId,
            expectedRevision = 1,
            content = new string('x', (256 * 1024) + 1)
        });
        Assert.Equal(
            "document_too_large",
            tooLarge.GetProperty("code").GetString());

        var unknown = await CallToolAsync(server.Client, token, "update_note", new
        {
            documentId,
            expectedRevision = 1,
            changes = new
            {
                unknown = 1
            }
        });
        Assert.Equal("invalid_input", unknown.GetProperty("code").GetString());
    }

    private static async Task<string> CreateTokenAsync(HttpClient client)
    {
        var csrf = await client.GetAntiforgeryTokenAsync();
        using var response = await client.PostWithAntiforgeryAsync(
            "/api/mcp/token/rotate",
            csrf);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement> CallToolAsync(
        HttpClient client,
        string token,
        string name,
        object arguments)
    {
        using var response = await PostToolAsync(client, token, name, arguments);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{name}: {body}");
        using var rpc = JsonDocument.Parse(ExtractJson(body));
        var text = rpc.RootElement
            .GetProperty("result")
            .GetProperty("content")
            .EnumerateArray()
            .First()
            .GetProperty("text")
            .GetString()!;
        using var toolResult = JsonDocument.Parse(text);
        var cloned = toolResult.RootElement.Clone();
        if (rpc.RootElement
            .GetProperty("result")
            .TryGetProperty("isError", out var isError)
            && isError.ValueKind == JsonValueKind.True)
        {
            return WrapError(cloned);
        }

        return cloned;
    }

    private static JsonElement WrapError(JsonElement toolError)
    {
        var payload = JsonSerializer.Serialize(new
        {
            _error = true,
            code = toolError.GetProperty("code").GetString(),
            message = toolError.GetProperty("message").GetString(),
            details = toolError.GetProperty("details")
        });
        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    private static string ExtractJson(string body)
    {
        var first = body.IndexOf('{');
        var last = body.LastIndexOf('}');
        Assert.True(
            first >= 0 && last > first,
            $"MCP response did not contain JSON: {body}");
        return body[first..(last + 1)];
    }

    private static Task<HttpResponseMessage> PostToolAsync(
        HttpClient client,
        string token,
        string name,
        object arguments,
        int? id = null)
    {
        var argumentsNode = JsonSerializer.SerializeToNode(arguments)
            as JsonObject ?? new JsonObject();
        if (IsSideEffectTool(name)
            && !argumentsNode.ContainsKey("idempotencyKey"))
        {
            argumentsNode["idempotencyKey"] =
                Guid.NewGuid().ToString("D");
        }
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id ?? Random.Shared.Next(10, int.MaxValue),
                    method = "tools/call",
                    @params = new
                    {
                        name,
                        arguments = argumentsNode
                    }
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client.SendAsync(request);
    }

    private static bool IsSideEffectTool(string name) =>
        name is
            "create_note"
            or "append_note"
            or "update_note"
            or "patch_note"
            or "delete_note"
            or "create_draft"
            or "update_draft"
            or "rebase_draft"
            or "submit_draft_for_review"
            or "discard_draft";
}
