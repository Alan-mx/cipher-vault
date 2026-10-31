using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Server.Api.Contracts;

namespace CipherVault.Server.Tests;

public sealed class VaultChangePasswordTests
{
    private const string NewPassword = "new correct horse battery staple";

    [Fact]
    public async Task SuccessfulChangeLocksVaultAndRotatesCredential()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        using var changed = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/vault/change-password",
            token,
            new VaultChangePasswordApiRequest(
                ServerTestApplication.Password,
                NewPassword));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var json = JsonDocument.Parse(
            await changed.Content.ReadAsStringAsync());
        Assert.Equal("locked", json.RootElement.GetProperty("state").GetString());

        using var oldClient = server.CreateClient();
        using var oldPassword = await oldClient.PostAsJsonAsync(
            "/api/vault/unlock",
            new VaultUnlockApiRequest(ServerTestApplication.Password));
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);

        using var newClient = server.CreateClient();
        using var newPassword = await newClient.PostAsJsonAsync(
            "/api/vault/unlock",
            new VaultUnlockApiRequest(NewPassword));
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
    }

    [Fact]
    public async Task FailedChangeKeepsVaultUsable()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        using var wrongOld = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/vault/change-password",
            token,
            new VaultChangePasswordApiRequest(
                "incorrect password",
                NewPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongOld.StatusCode);

        using var shortNew = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/vault/change-password",
            token,
            new VaultChangePasswordApiRequest(
                ServerTestApplication.Password,
                "short"));
        Assert.Equal(HttpStatusCode.BadRequest, shortNew.StatusCode);

        using var status = await server.Client.GetAsync("/api/vault/status");
        using var json = JsonDocument.Parse(
            await status.Content.ReadAsStringAsync());
        Assert.Equal("unlocked", json.RootElement.GetProperty("state").GetString());
    }
}
