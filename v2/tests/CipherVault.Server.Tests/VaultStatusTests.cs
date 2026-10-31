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
        Assert.Equal(10, root.GetProperty("idleTimeoutMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("idleRemainingSeconds").ValueKind);
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
        Assert.Equal(10, root.GetProperty("idleTimeoutMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("idleRemainingSeconds").ValueKind);
    }

    [Fact]
    public async Task UnlockedStatusExposesServerMonotonicIdleRemaining()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);

        using var immediate = await server.Client.GetAsync("/api/vault/status");
        using var immediateJson = JsonDocument.Parse(
            await immediate.Content.ReadAsStringAsync());
        Assert.Equal(
            600,
            immediateJson.RootElement.GetProperty("idleRemainingSeconds").GetInt32());

        server.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        using var halfway = await server.Client.GetAsync("/api/vault/status");
        using var halfwayJson = JsonDocument.Parse(
            await halfway.Content.ReadAsStringAsync());
        Assert.Equal(
            300,
            halfwayJson.RootElement.GetProperty("idleRemainingSeconds").GetInt32());

        _ = await server.Manager.LockAsync();
        using var locked = await server.Client.GetAsync("/api/vault/status");
        using var lockedJson = JsonDocument.Parse(
            await locked.Content.ReadAsStringAsync());
        Assert.Equal(
            JsonValueKind.Null,
            lockedJson.RootElement.GetProperty("idleRemainingSeconds").ValueKind);
    }
}
