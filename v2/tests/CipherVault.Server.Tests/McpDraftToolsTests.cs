using System.Net;
using System.Text;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class McpDraftToolsTests
{
    [Fact]
    public async Task DraftToolsWorkAndFormalWritesFollowCurrentMode()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await CreateTokenAsync(server.Client);

        var created = await CallToolAsync(
            server.Client,
            token,
            "create_draft",
            new
            {
                draftType = "create",
                payload = new
                {
                    title = "MCP Draft",
                    virtualPath = "notes/mcp-draft.md",
                    format = "md",
                    content = "# MCP Draft\n",
                    tags = Array.Empty<string>(),
                },
                idempotencyKey = Guid.NewGuid().ToString("D"),
            });
        var draftId = created.GetProperty("draftId").GetGuid();
        var revision = created.GetProperty("draftRevision").GetInt32();

        var page = await CallToolAsync(
            server.Client,
            token,
            "list_drafts",
            new { status = "draft" });
        Assert.Contains(
            page.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("draftId").GetGuid() == draftId);

        var detail = await CallToolAsync(
            server.Client,
            token,
            "read_draft",
            new { draftId = draftId.ToString("D") });
        Assert.Contains(
            "# MCP Draft",
            detail.GetProperty("preview").GetProperty("content").GetString());

        var reviewed = await CallToolAsync(
            server.Client,
            token,
            "submit_draft_for_review",
            new
            {
                draftId = draftId.ToString("D"),
                expectedDraftRevision = revision,
                idempotencyKey = Guid.NewGuid().ToString("D"),
            });
        Assert.Equal(
            "pending_review",
            reviewed.GetProperty("status").GetString());

        var blocked = await CallToolAsync(
            server.Client,
            token,
            "create_note",
            new
            {
                title = "Blocked",
                content = "# Blocked\n",
                idempotencyKey = Guid.NewGuid().ToString("D"),
            });
        Assert.Equal(
            "write_mode_draft_required",
            blocked.GetProperty("code").GetString());

        await server.Client.SetMcpWriteModeAsync("direct");
        var formalKey = Guid.NewGuid().ToString("D");
        var formal = await CallToolAsync(
            server.Client,
            token,
            "create_note",
            new
            {
                title = "Formal",
                content = "# Formal\n",
                idempotencyKey = formalKey,
            });
        Assert.Equal(1, formal.GetProperty("revision").GetInt32());
        var replay = await CallToolAsync(
            server.Client,
            token,
            "create_note",
            new
            {
                title = "Formal",
                content = "# Formal\n",
                idempotencyKey = formalKey,
            });
        Assert.Equal(
            formal.GetProperty("documentId").GetGuid(),
            replay.GetProperty("documentId").GetGuid());

        var reused = await CallToolAsync(
            server.Client,
            token,
            "create_note",
            new
            {
                title = "Different",
                content = "# Different\n",
                idempotencyKey = formalKey,
            });
        Assert.Equal(
            "idempotency_key_reused",
            reused.GetProperty("code").GetString());
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
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = Random.Shared.Next(10, int.MaxValue),
                    method = "tools/call",
                    @params = new { name, arguments },
                }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                token);
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(
                "application/json"));
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(
                "text/event-stream"));
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var rpc = JsonDocument.Parse(ExtractJson(body));
        var result = rpc.RootElement.GetProperty("result");
        if (result.TryGetProperty("structuredContent", out var structured))
        {
            return structured.Clone();
        }
        var text = result.GetProperty("content")
            .EnumerateArray()
            .First()
            .GetProperty("text")
            .GetString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
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
}
