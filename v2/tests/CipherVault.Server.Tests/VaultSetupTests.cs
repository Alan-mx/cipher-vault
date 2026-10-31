using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Server.Api.Contracts;

namespace CipherVault.Server.Tests;

public sealed class VaultSetupTests
{
    [Fact]
    public async Task SetupRequiresRiskAcknowledgementWithoutCreatingVault()
    {
        await using var server = await ServerTestApplication.CreateAsync();

        using var response = await server.Client.PostAsJsonAsync(
            "/api/vault/setup",
            new VaultSetupApiRequest(
                ServerTestApplication.Password,
                RiskAccepted: false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(
            Path.Combine(server.Options.Paths.VaultDirectory, "vault.db")));
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "invalid_input",
            json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SetupCreatesLockedVaultAndIsOnlyAvailableOnce()
    {
        await using var server = await ServerTestApplication.CreateAsync();
        using var response = await server.Client.PostAsJsonAsync(
            "/api/vault/setup",
            new VaultSetupApiRequest(
                ServerTestApplication.Password,
                RiskAccepted: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal("locked", json.RootElement.GetProperty("state").GetString());
        Assert.True(File.Exists(
            Path.Combine(server.Options.Paths.VaultDirectory, "vault.db")));

        using var second = await server.Client.PostAsJsonAsync(
            "/api/vault/setup",
            new VaultSetupApiRequest(
                ServerTestApplication.Password,
                RiskAccepted: true));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var error = JsonDocument.Parse(
            await second.Content.ReadAsStringAsync());
        Assert.Equal(
            "vault_already_initialized",
            error.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SetupRejectsShortPassword()
    {
        await using var server = await ServerTestApplication.CreateAsync();

        using var response = await server.Client.PostAsJsonAsync(
            "/api/vault/setup",
            new VaultSetupApiRequest("short", RiskAccepted: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(
            Path.Combine(server.Options.Paths.VaultDirectory, "vault.db")));
    }
}
