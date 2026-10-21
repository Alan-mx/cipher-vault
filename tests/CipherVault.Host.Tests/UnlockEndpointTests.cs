using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class UnlockEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PasswordUnlockCreatesGlobalSessionAndReusesIt()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var first = CreateRequest("password");

        using var firstResponse = await fixture.Client.SendAsync(first);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var firstPayload = await firstResponse.Content.ReadFromJsonAsync<
            HostApiResponse<HostSessionStatusData>>(JsonOptions);
        Assert.NotNull(firstPayload);
        Assert.Equal("unlocked", firstPayload.Data.State);
        Assert.NotNull(firstPayload.Data.SessionId);
        Assert.Equal(1, fixture.CredentialPrompt.RequestCount);
        Assert.Equal(
            HostCredentialPreference.Password,
            fixture.CredentialPrompt.LastPreference);

        using var second = CreateRequest("password");
        using var secondResponse = await fixture.Client.SendAsync(second);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(1, fixture.CredentialPrompt.RequestCount);
    }

    [Fact]
    public async Task RecoveryKeyUnlockWorks()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        fixture.CredentialPrompt.Result = new HostCredentialPromptResult(
            true,
            Assert.IsType<string>(fixture.RecoveryKey),
            VaultCredentialType.RecoveryKey);
        using var request = CreateRequest("recoveryKey");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostSessionStatusData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("unlocked", payload.Data.State);
        Assert.Equal(
            HostCredentialPreference.RecoveryKey,
            fixture.CredentialPrompt.LastPreference);
    }

    [Fact]
    public async Task CancelledAuthorizationReturnsAccessDenied()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        fixture.CredentialPrompt.Result = new HostCredentialPromptResult(
            false,
            null,
            VaultCredentialType.Password);
        using var request = CreateRequest("password");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("access_denied", payload.Error.Code);
        Assert.Equal("locked", (await GetStatusAsync(fixture)).Session.State);
    }

    [Fact]
    public async Task WrongPasswordReturnsInvalidCredential()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        fixture.CredentialPrompt.Result = new HostCredentialPromptResult(
            true,
            "wrong password",
            VaultCredentialType.Password);
        using var request = CreateRequest("password");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_credential", payload.Error.Code);
        Assert.Equal("locked", (await GetStatusAsync(fixture)).Session.State);
    }

    [Fact]
    public async Task InvalidCredentialTypeReturnsInvalidInput()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest("unsupported");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_input", payload.Error.Code);
        Assert.Equal(0, fixture.CredentialPrompt.RequestCount);
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

    private static HttpRequestMessage CreateRequest(string credentialType)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/session/unlock")
        {
            Content = JsonContent.Create(new HostUnlockRequest(credentialType))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        return request;
    }
}
