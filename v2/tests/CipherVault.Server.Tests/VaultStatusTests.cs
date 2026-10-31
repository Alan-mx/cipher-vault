using System.Net;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class VaultStatusTests
{
    [Fact]
    public async Task UninitializedStatusIsSafeAndNotCached()
    {
        await using var server = await ServerTestApplication.CreateAsync();

        using var response = await server.Client.GetAsync("/api/vault/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("uninitialized", root.GetProperty("state").GetString());
        Assert.False(root.GetProperty("isInitialized").GetBoolean());
        Assert.False(root.GetProperty("isUnlocked").GetBoolean());
        Assert.False(root.TryGetProperty("sessionId", out _));
        Assert.False(root.TryGetProperty("vaultId", out _));
    }

    [Fact]
    public async Task InitializedVaultStartsLocked()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);

        using var response = await server.Client.GetAsync("/api/vault/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("locked", root.GetProperty("state").GetString());
        Assert.True(root.GetProperty("isInitialized").GetBoolean());
        Assert.False(root.GetProperty("isUnlocked").GetBoolean());
    }
}
