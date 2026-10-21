using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class ConfirmRecoveryEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ConfirmRecoveryActivatesRotatedKey()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var recoveryKey = await RotateRecoveryAsync(fixture);
        fixture.CredentialPrompt.EnqueueResult(RecoveryKeyResult(recoveryKey));
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await LockHostAsync(fixture.Client);
        fixture.CredentialPrompt.Result = RecoveryKeyResult(recoveryKey);
        await UnlockHostAsync(fixture.Client);
    }

    [Fact]
    public async Task ConfirmRecoveryRejectsCancelledAuthorization()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        fixture.CredentialPrompt.EnqueueResult(new HostCredentialPromptResult(
            false,
            null,
            VaultCredentialType.RecoveryKey));
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("access_denied", payload.Error.Code);
    }

    [Fact]
    public async Task ConfirmRecoveryWorksWhileVaultIsLocked()
    {
        await using var fixture = await HostTestFixture.CreateAsync(
            confirmRecoveryKey: false);
        Assert.NotNull(fixture.RecoveryKey);
        fixture.CredentialPrompt.EnqueueResult(RecoveryKeyResult(fixture.RecoveryKey));
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var status = await UnlockHostAsync(fixture.Client);
        Assert.True(status.RecoveryVerified);
    }

    [Fact]
    public async Task FirstUseFlowInitializesConfirmsAndUnlocks()
    {
        await using var fixture = await HostTestFixture.CreateAsync(
            initializeVault: false);
        fixture.CredentialPrompt.EnqueueResult(PasswordResult(
            HostTestFixture.VaultPassword));
        fixture.CredentialPrompt.EnqueueResult(PasswordResult(
            HostTestFixture.VaultPassword));
        using var initializeRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/vault/initialize")
        {
            Content = JsonContent.Create(new HostInitializeRequest(fixture.VaultPath))
        };
        initializeRequest.Headers.Host = "127.0.0.1:43127";
        initializeRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var initializeResponse = await fixture.Client.SendAsync(
            initializeRequest);
        initializeResponse.EnsureSuccessStatusCode();
        var initialized = await initializeResponse.Content.ReadFromJsonAsync<
            HostApiResponse<HostInitializeData>>(JsonOptions);
        Assert.NotNull(initialized);
        Assert.False(string.IsNullOrWhiteSpace(initialized.Data.RecoveryKey));

        var locked = await GetStatusAsync(fixture.Client);
        Assert.Equal(
            VaultSessionState.Locked,
            Enum.Parse<VaultSessionState>(locked.State, ignoreCase: true));
        Assert.False(locked.RecoveryVerified);

        fixture.CredentialPrompt.EnqueueResult(RecoveryKeyResult(
            initialized.Data.RecoveryKey));
        using var confirmRequest = CreateRequest();
        confirmRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var confirmResponse = await fixture.Client.SendAsync(confirmRequest);
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);

        var unlocked = await UnlockHostAsync(fixture.Client);
        Assert.Equal(
            VaultSessionState.Unlocked,
            Enum.Parse<VaultSessionState>(unlocked.State, ignoreCase: true));
        Assert.True(unlocked.RecoveryVerified);

        using var catalogRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/v1/catalog");
        catalogRequest.Headers.Host = "127.0.0.1:43127";
        catalogRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var catalogResponse = await fixture.Client.SendAsync(catalogRequest);
        catalogResponse.EnsureSuccessStatusCode();
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<
            HostApiResponse<VaultCatalogPage>>(JsonOptions);
        Assert.NotNull(catalog);
        Assert.Empty(catalog.Data.Items);
    }

    private static async Task<string> RotateRecoveryAsync(HostTestFixture fixture)
    {
        fixture.CredentialPrompt.EnqueueResult(PasswordResult(
            HostTestFixture.VaultPassword));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/maintenance/recovery/rotate");
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostRecoveryRotationData>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data.RecoveryKey;
    }

    private static async Task<HostSessionStatusData> UnlockHostAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/session/unlock")
        {
            Content = JsonContent.Create(new HostUnlockRequest("any"))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostSessionStatusData>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data;
    }

    private static async Task<HostSessionStatusData> GetStatusAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/v1/status");
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostStatusData>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data.Session;
    }

    private static async Task LockHostAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/session/lock")
        {
            Content = JsonContent.Create(new HostLockRequest("recovery-confirm-test"))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static HostCredentialPromptResult PasswordResult(string password)
    {
        return new HostCredentialPromptResult(
            true,
            password,
            VaultCredentialType.Password);
    }

    private static HostCredentialPromptResult RecoveryKeyResult(string recoveryKey)
    {
        return new HostCredentialPromptResult(
            true,
            recoveryKey,
            VaultCredentialType.RecoveryKey);
    }

    private static HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/maintenance/recovery/confirm");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
