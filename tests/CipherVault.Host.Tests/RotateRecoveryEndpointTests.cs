using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class RotateRecoveryEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RotateRecoveryReturnsNewKeyOnce()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        fixture.CredentialPrompt.EnqueueResult(PasswordResult(
            HostTestFixture.VaultPassword));
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostRecoveryRotationData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload.Data.RecoveryKey));
        Assert.NotEqual(fixture.RecoveryKey, payload.Data.RecoveryKey);
    }

    [Fact]
    public async Task RotateRecoveryRejectsCancelledAuthorization()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        fixture.CredentialPrompt.EnqueueResult(new HostCredentialPromptResult(
            false,
            null,
            VaultCredentialType.Password));
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
    public async Task RotateRecoveryRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode)423, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("session_locked", payload.Error.Code);
    }

    private static async Task UnlockHostAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/session/unlock")
        {
            Content = JsonContent.Create(new HostUnlockRequest("password"))
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

    private static HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/maintenance/recovery/rotate");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
