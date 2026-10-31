using System.Net;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class VaultActivityTests
{
    [Fact]
    public async Task ActivityRefreshesExpiryAndRequiresCsrf()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        server.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        using var withoutCsrf = await server.Client.PostAsync(
            "/api/vault/activity",
            content: null);
        Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);

        using var refreshed = await server.Client.PostWithAntiforgeryAsync(
            "/api/vault/activity",
            token);
        Assert.Equal(HttpStatusCode.NoContent, refreshed.StatusCode);

        using var status = await server.Client.GetAsync("/api/vault/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var json = JsonDocument.Parse(
            await status.Content.ReadAsStringAsync());
        var expires = json.RootElement
            .GetProperty("idleExpiresAtUtc")
            .GetDateTimeOffset();
        Assert.Equal(
            server.TimeProvider.GetUtcNow().AddMinutes(10),
            expires);
    }

    [Fact]
    public async Task ExplicitActivityIsLimitedPerVaultSession()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        using var first = await server.Client.PostWithAntiforgeryAsync(
            "/api/vault/activity",
            token);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        server.TimeProvider.Advance(TimeSpan.FromSeconds(10));
        using var limited = await server.Client.PostWithAntiforgeryAsync(
            "/api/vault/activity",
            token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("20", limited.Headers.RetryAfter?.ToString());
        using var limitedJson = JsonDocument.Parse(
            await limited.Content.ReadAsStringAsync());
        Assert.Equal(
            "activity_rate_limited",
            limitedJson.RootElement.GetProperty("code").GetString());

        server.TimeProvider.Advance(TimeSpan.FromSeconds(20));
        using var afterWindow = await server.Client.PostWithAntiforgeryAsync(
            "/api/vault/activity",
            token);
        Assert.Equal(HttpStatusCode.NoContent, afterWindow.StatusCode);

        _ = await server.Manager.LockAsync();
        using var nextUnlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, nextUnlock.StatusCode);
        var nextToken = await server.Client.GetAntiforgeryTokenAsync();
        using var newSession = await server.Client.PostWithAntiforgeryAsync(
            "/api/vault/activity",
            nextToken);
        Assert.Equal(HttpStatusCode.NoContent, newSession.StatusCode);
    }
}
