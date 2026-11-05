using System.Net;
using System.Text.Json;
using CipherVault.Server.Mcp;

namespace CipherVault.Server.Tests;

public sealed class McpInfoApiTests
{
    [Fact]
    public async Task ReturnsConnectionInfoAndCapabilitiesWithoutSecrets()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var anonymous = await server.CreateClient()
            .GetAsync("/api/mcp/info");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var unlock = await server.Client.UnlockAsync();
        unlock.EnsureSuccessStatusCode();
        using var response = await server.Client.GetAsync("/api/mcp/info");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("streamable_http", root.GetProperty("transport").GetString());
        Assert.Equal("/mcp", root.GetProperty("endpointPath").GetString());
        Assert.True(root.GetProperty("draftOnly").GetBoolean());
        Assert.True(root.GetProperty("requiresVaultUnlock").GetBoolean());
        Assert.Equal(
            McpToolNames.All.Order(StringComparer.Ordinal),
            root.GetProperty("tools")
                .EnumerateArray()
                .Select(item => item.GetProperty("name").GetString()!)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            McpPromptNames.All.Order(StringComparer.Ordinal),
            root.GetProperty("prompts")
                .EnumerateArray()
                .Select(item => item.GetProperty("name").GetString()!)
                .Order(StringComparer.Ordinal));
    }
}
