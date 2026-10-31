using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Server.Api.Contracts;

namespace CipherVault.Server.Tests;

public sealed class VaultUnlockTests
{
    [Fact]
    public async Task CorrectPasswordUnlocksAndIssuesSessionCookie()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);

        using var response = await server.Client.PostAsJsonAsync(
            "/api/vault/unlock",
            new VaultUnlockApiRequest(ServerTestApplication.Password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(
            cookies,
            value => value.StartsWith(
                "CipherVault.Session=",
                StringComparison.Ordinal));
        Assert.Contains(
            cookies,
            value => value.Contains("httponly", StringComparison.OrdinalIgnoreCase)
                && value.Contains(
                    "samesite=strict",
                    StringComparison.OrdinalIgnoreCase)
                && !value.Contains("expires=", StringComparison.OrdinalIgnoreCase)
                && !value.Contains("max-age=", StringComparison.OrdinalIgnoreCase));
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal("unlocked", json.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task UnlockingAnUnlockedVaultStillVerifiesPassword()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var anonymous = server.CreateClient();

        using var wrong = await anonymous.PostAsJsonAsync(
            "/api/vault/unlock",
            new VaultUnlockApiRequest("incorrect password"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(wrong.Headers.TryGetValues("Set-Cookie", out _));

        using var valid = await anonymous.PostAsJsonAsync(
            "/api/vault/unlock",
            new VaultUnlockApiRequest(ServerTestApplication.Password));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.True(valid.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task FiveFailuresTriggerSixtySecondCooldown()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failure = await server.Client.PostAsJsonAsync(
                "/api/vault/unlock",
                new VaultUnlockApiRequest("incorrect password"));
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        using var limited = await server.Client.PostAsJsonAsync(
            "/api/vault/unlock",
            new VaultUnlockApiRequest(ServerTestApplication.Password));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.RetryAfter?.ToString());

        server.TimeProvider.Advance(TimeSpan.FromSeconds(61));
        using var success = await server.Client.PostAsJsonAsync(
            "/api/vault/unlock",
            new VaultUnlockApiRequest(ServerTestApplication.Password));
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }

    [Fact]
    public async Task UnlockBeforeSetupReturnsLockedStateError()
    {
        await using var server = await ServerTestApplication.CreateAsync();

        using var response = await server.Client.PostAsJsonAsync(
            "/api/vault/unlock",
            new VaultUnlockApiRequest(ServerTestApplication.Password));

        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "vault_not_initialized",
            json.RootElement.GetProperty("code").GetString());
    }
}
