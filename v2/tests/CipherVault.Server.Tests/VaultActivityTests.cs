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
}
