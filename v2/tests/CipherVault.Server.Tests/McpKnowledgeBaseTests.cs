using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class McpKnowledgeBaseTests
{
    [Fact]
    public async Task ExposesKnowledgeModelResourceAndDraftOnlyTools()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        unlock.EnsureSuccessStatusCode();
        var token = await McpTestClient.CreateTokenAsync(server.Client);

        using var tools = await RpcAsync(
            server.Client,
            token,
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
        var toolNames = tools.RootElement.GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("get_knowledge_model", toolNames);
        Assert.Contains("search_knowledge", toolNames);
        Assert.Contains("create_draft", toolNames);

        using var resources = await RpcAsync(
            server.Client,
            token,
            """{"jsonrpc":"2.0","id":2,"method":"resources/list","params":{}}""");
        Assert.Contains(
            resources.RootElement.GetProperty("result")
                .GetProperty("resources")
                .EnumerateArray(),
            item => item.GetProperty("uri").GetString()
                == "cipher-vault://knowledge-model");

        using var read = await RpcAsync(
            server.Client,
            token,
            """{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"cipher-vault://knowledge-model"}}""");
        var text = read.RootElement.GetProperty("result")
            .GetProperty("contents")[0]
            .GetProperty("text")
            .GetString();
        Assert.Contains("\"knowledgeTypes\"", text);
    }

    private static async Task<JsonDocument> RpcAsync(
        HttpClient client,
        string token,
        string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, responseBody);
        var first = responseBody.IndexOf('{');
        var last = responseBody.LastIndexOf('}');
        Assert.True(first >= 0 && last > first, responseBody);
        return JsonDocument.Parse(responseBody[first..(last + 1)]);
    }
}
