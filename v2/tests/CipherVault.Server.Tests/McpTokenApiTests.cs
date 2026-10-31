using System.Net;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class McpTokenApiTests
{
    [Fact]
    public async Task StatusAndRotateReturnSafeOneTimeToken()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var initial = await server.Client.GetAsync(
            "/api/mcp/token/status");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.Equal("no-store", initial.Headers.CacheControl?.ToString());
        using var initialJson = JsonDocument.Parse(
            await initial.Content.ReadAsStringAsync());
        Assert.False(
            initialJson.RootElement.GetProperty("configured").GetBoolean());

        using var missingCsrf = await server.Client.PostAsync(
            "/api/mcp/token/rotate",
            content: null);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        using var rotated = await server.Client.PostWithAntiforgeryAsync(
            "/api/mcp/token/rotate",
            csrf);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Equal("no-store", rotated.Headers.CacheControl?.ToString());
        using var rotatedJson = JsonDocument.Parse(
            await rotated.Content.ReadAsStringAsync());
        var token = rotatedJson.RootElement.GetProperty("token").GetString();
        Assert.NotNull(token);
        Assert.Equal(43, token.Length);
        Assert.True(server.TokenStore.TryAuthenticate(token));

        using var configured = await server.Client.GetAsync(
            "/api/mcp/token/status");
        var statusText = await configured.Content.ReadAsStringAsync();
        Assert.True(
            JsonDocument.Parse(statusText)
                .RootElement.GetProperty("configured").GetBoolean());
        Assert.DoesNotContain(token, statusText, StringComparison.Ordinal);
        Assert.DoesNotContain("digest", statusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TokenManagementRequiresUnlockedWebSession()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);

        using var status = await server.Client.GetAsync(
            "/api/mcp/token/status");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);

        using var rotate = await server.Client.PostAsync(
            "/api/mcp/token/rotate",
            content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, rotate.StatusCode);
    }
}
