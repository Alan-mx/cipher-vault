using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CipherVault.Server.Tests;

internal static class McpTestClient
{
    internal static async Task<string> CreateTokenAsync(HttpClient client)
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

    internal static async Task<JsonElement> CallToolAsync(
        HttpClient client,
        string token,
        string name,
        object arguments)
    {
        var request = CreateRequest(
            token,
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = Random.Shared.Next(10, int.MaxValue),
                method = "tools/call",
                @params = new { name, arguments }
            }));
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

    internal static async Task<JsonElement> ListToolsAsync(
        HttpClient client,
        string token)
    {
        var request = CreateRequest(
            token,
            """
            {
              "jsonrpc": "2.0",
              "id": 2,
              "method": "tools/list",
              "params": {}
            }
            """);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(ExtractJson(body))
            .RootElement
            .GetProperty("result")
            .Clone();
    }

    private static HttpRequestMessage CreateRequest(string token, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                body,
                Encoding.UTF8,
                "application/json")
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
        return request;
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
