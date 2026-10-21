using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class InitializeEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task InitializeCreatesVaultAndReturnsRecoveryKeyOnce()
    {
        await using var fixture = await HostTestFixture.CreateAsync(
            initializeVault: false);
        EnqueuePasswordPair(fixture.CredentialPrompt, "new vault password");
        using var request = CreateRequest(fixture.VaultPath);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostInitializeData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.True(payload.Ok);
        Assert.Equal(fixture.VaultPath, payload.Data.VaultPath);
        Assert.False(string.IsNullOrWhiteSpace(payload.Data.RecoveryKey));
        Assert.True(Directory.Exists(fixture.VaultPath));
        Assert.Equal(2, fixture.CredentialPrompt.RequestCount);
        Assert.Contains("再次输入", fixture.CredentialPrompt.LastMessage);

        var status = await GetStatusAsync(fixture);
        Assert.Equal("locked", status.Session.State);
    }

    [Fact]
    public async Task PasswordMismatchDoesNotCreateVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync(
            initializeVault: false);
        fixture.CredentialPrompt.EnqueueResult(new HostCredentialPromptResult(
            true,
            "password-one",
            VaultCredentialType.Password));
        fixture.CredentialPrompt.EnqueueResult(new HostCredentialPromptResult(
            true,
            "password-two",
            VaultCredentialType.Password));
        using var request = CreateRequest(fixture.VaultPath);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_input", payload.Error.Code);
        Assert.False(Directory.Exists(fixture.VaultPath));
    }

    [Fact]
    public async Task ExistingVaultCannotBeInitializedAgain()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        EnqueuePasswordPair(fixture.CredentialPrompt, "new vault password");
        using var request = CreateRequest(fixture.VaultPath);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("already_exists", payload.Error.Code);
        Assert.Equal(0, fixture.CredentialPrompt.RequestCount);
    }

    [Fact]
    public async Task InitializationTargetMustMatchConfiguredVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync(
            initializeVault: false);
        using var request = CreateRequest(
            Path.Combine(fixture.Root, "different-vault"));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_input", payload.Error.Code);
    }

    private static void EnqueuePasswordPair(
        HostTestFixture.TestCredentialPrompt prompt,
        string password)
    {
        prompt.EnqueueResult(new HostCredentialPromptResult(
            true,
            password,
            VaultCredentialType.Password));
        prompt.EnqueueResult(new HostCredentialPromptResult(
            true,
            password,
            VaultCredentialType.Password));
    }

    private static async Task<HostStatusData> GetStatusAsync(HostTestFixture fixture)
    {
        using var request = HostTestFixture.CreateRequest("/admin/v1/status");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostStatusData>>(JsonOptions);
        return Assert.IsType<HostStatusData>(payload?.Data);
    }

    private static HttpRequestMessage CreateRequest(string vaultPath)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/vault/initialize")
        {
            Content = JsonContent.Create(new HostInitializeRequest(vaultPath))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        return request;
    }
}
