using System.Net;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class AntiforgeryAndLockTests
{
    [Fact]
    public async Task AntiforgeryRequiresAuthentication()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);

        using var response = await server.Client.GetAsync(
            "/api/vault/antiforgery");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "authentication_required",
            json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task LockRequiresCsrfAndInvalidatesCookie()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var missing = await server.Client.PostAsync(
            "/api/vault/lock",
            content: null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        using var wrong = await server.Client.PostWithAntiforgeryAsync(
            "/api/vault/lock",
            "invalid-token");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var token = await server.Client.GetAntiforgeryTokenAsync();
        using var locked = await server.Client.PostWithAntiforgeryAsync(
            "/api/vault/lock",
            token);
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        using var statusJson = JsonDocument.Parse(
            await locked.Content.ReadAsStringAsync());
        Assert.Equal(
            "locked",
            statusJson.RootElement.GetProperty("state").GetString());

        using var rejected = await server.Client.GetAsync(
            "/api/vault/antiforgery");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }
}
