using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class McpEndpointAuthenticationTests
{
    [Fact]
    public async Task UnconfiguredTokenReturns503()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);

        using var response = await PostMcpAsync(
            server.Client,
            token: null,
            InitializeJson());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(
            "mcp_token_not_configured",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidCookieAndInvalidBearerAreRejected()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        using var rotated = await server.Client.PostWithAntiforgeryAsync(
            "/api/mcp/token/rotate",
            csrf);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        using var rotatedJson = JsonDocument.Parse(
            await rotated.Content.ReadAsStringAsync());
        var token = rotatedJson.RootElement.GetProperty("token").GetString()!;

        using var cookieOnly = await PostMcpAsync(
            server.Client,
            token: null,
            InitializeJson());
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);

        using var wrong = await PostMcpAsync(
            server.Client,
            "wrong-token",
            InitializeJson());
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(
            "Bearer",
            wrong.Headers.WwwAuthenticate.Single().Scheme);

        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                InitializeJson(),
                Encoding.UTF8,
                "application/json")
        };
        malformed.Headers.Authorization =
            new AuthenticationHeaderValue("Basic", "not-bearer");
        using var malformedResponse = await server.Client.SendAsync(malformed);
        Assert.Equal(HttpStatusCode.Unauthorized, malformedResponse.StatusCode);

        using var duplicate = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                InitializeJson(),
                Encoding.UTF8,
                "application/json")
        };
        Assert.True(duplicate.Headers.TryAddWithoutValidation(
            "Authorization",
            new[] { $"Bearer {token}", $"Bearer {token}" }));
        using var duplicateResponse = await server.Client.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.Unauthorized, duplicateResponse.StatusCode);
    }

    [Fact]
    public async Task ValidBearerCanInitializeStatelessTransport()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        using var rotated = await server.Client.PostWithAntiforgeryAsync(
            "/api/mcp/token/rotate",
            csrf);
        using var rotatedJson = JsonDocument.Parse(
            await rotated.Content.ReadAsStringAsync());
        var token = rotatedJson.RootElement.GetProperty("token").GetString()!;

        using var response = await PostMcpAsync(
            server.Client,
            token,
            InitializeJson());
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {body}");
        Assert.Contains("\"result\"", body, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            "nosniff",
            response.Headers.GetValues("X-Content-Type-Options").Single());

        using var get = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        get.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        get.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var getResponse = await server.Client.SendAsync(get);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getResponse.StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        delete.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        using var deleteResponse = await server.Client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task ToolsListContainsFifteenToolsAndVaultStatusCallable()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        using var rotated = await server.Client.PostWithAntiforgeryAsync(
            "/api/mcp/token/rotate",
            csrf);
        using var rotatedJson = JsonDocument.Parse(
            await rotated.Content.ReadAsStringAsync());
        var token = rotatedJson.RootElement.GetProperty("token").GetString()!;

        using var initialize = await PostMcpAsync(
            server.Client,
            token,
            InitializeJson());
        Assert.True(initialize.IsSuccessStatusCode);

        using var list = await PostMcpAsync(
            server.Client,
            token,
            """
            {
              "jsonrpc": "2.0",
              "id": 2,
              "method": "tools/list",
              "params": {}
            }
            """);
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.True(list.IsSuccessStatusCode, listBody);
        var expected = new[]
        {
            "vault_status", "list_notes", "read_note", "search_notes",
            "scan_notes", "list_tags", "get_document_map", "get_backlinks",
            "find_unlinked_mentions", "get_local_graph", "create_note",
            "append_note", "update_note", "patch_note", "delete_note"
        };
        Assert.All(
            expected,
            name => Assert.Contains(
                $"\"name\":\"{name}\"",
                listBody,
                StringComparison.Ordinal));
        using var listJson = JsonDocument.Parse(ExtractJson(listBody));
        var actual = listJson.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expected.Order(StringComparer.Ordinal).ToArray(),
            actual);

        using var call = await PostMcpAsync(
            server.Client,
            token,
            """
            {
              "jsonrpc": "2.0",
              "id": 3,
              "method": "tools/call",
              "params": {
                "name": "vault_status",
                "arguments": {}
              }
            }
            """);
        var callBody = await call.Content.ReadAsStringAsync();
        Assert.True(call.IsSuccessStatusCode, callBody);
        Assert.Contains("unlocked", callBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"isError\":true", callBody, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> PostMcpAsync(
        HttpClient client,
        string? token,
        string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
        return client.SendAsync(request);
    }

    private static string InitializeJson() =>
        """
        {
          "jsonrpc": "2.0",
          "id": 1,
          "method": "initialize",
          "params": {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {
              "name": "CipherVault.Server.Tests",
              "version": "1.0"
            }
          }
        }
        """;

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
