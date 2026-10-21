using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class AccessGrantEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RequestGrantReturnsSecretAndPromptMetadata()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateAgentAsync(
            fixture.Client,
            "grant-agent",
            "readOnly",
            "accessGrant");
        var clientInstanceId = Guid.NewGuid();

        using var response = await SendGrantRequestAsync(
            fixture.Client,
            token,
            clientInstanceId,
            "read");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostIssuedAccessGrantData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("grant-agent", payload.Data.Grant.ClientId);
        Assert.Equal(clientInstanceId, payload.Data.Grant.ClientInstanceId);
        Assert.Equal(["read"], payload.Data.Grant.Scopes);
        Assert.Equal("active", payload.Data.Grant.State);
        Assert.False(string.IsNullOrWhiteSpace(payload.Data.Secret));
        Assert.Equal(1, fixture.AccessGrantPrompt.RequestCount);
        Assert.NotNull(fixture.AccessGrantPrompt.LastRequest);
        Assert.Equal(
            AccessGrantScope.Read,
            fixture.AccessGrantPrompt.LastRequest.RequestedScopes);
    }

    [Fact]
    public async Task ActiveGrantForSameInstanceReturnsConflict()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateAgentAsync(
            fixture.Client,
            "grant-agent",
            "readOnly",
            "accessGrant");
        var clientInstanceId = Guid.NewGuid();
        using (var first = await SendGrantRequestAsync(
                   fixture.Client,
                   token,
                   clientInstanceId,
                   "read"))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        using var second = await SendGrantRequestAsync(
            fixture.Client,
            token,
            clientInstanceId,
            "read");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var error = await second.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("conflict", error.Error.Code);
    }

    [Fact]
    public async Task DeniedGrantStartsClientCooldown()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateAgentAsync(
            fixture.Client,
            "grant-agent",
            "readOnly",
            "accessGrant");
        fixture.AccessGrantPrompt.EnqueueResult(
            new HostAccessGrantPromptResult(
                false,
                AccessGrantScope.None));

        using var denied = await SendGrantRequestAsync(
            fixture.Client,
            token,
            Guid.NewGuid(),
            "read");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedError = await denied.Content.ReadFromJsonAsync<
            HostApiErrorResponse>(JsonOptions);
        Assert.NotNull(deniedError);
        Assert.Equal("access_denied", deniedError.Error.Code);

        using var cooled = await SendGrantRequestAsync(
            fixture.Client,
            token,
            Guid.NewGuid(),
            "read");
        Assert.Equal((HttpStatusCode)429, cooled.StatusCode);
        var cooledError = await cooled.Content.ReadFromJsonAsync<
            HostApiErrorResponse>(JsonOptions);
        Assert.NotNull(cooledError);
        Assert.Equal("access_grant_cooldown", cooledError.Error.Code);
    }

    [Fact]
    public async Task LockedVaultDoesNotPromptForGrant()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var token = await CreateAgentAsync(
            fixture.Client,
            "grant-agent",
            "readOnly",
            "accessGrant");

        using var response = await SendGrantRequestAsync(
            fixture.Client,
            token,
            Guid.NewGuid(),
            "read");

        Assert.Equal((HttpStatusCode)423, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("session_locked", error.Error.Code);
        Assert.Equal(0, fixture.AccessGrantPrompt.RequestCount);
    }

    [Fact]
    public async Task StaticTokenClientCannotRequestGrant()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateAgentAsync(
            fixture.Client,
            "static-agent",
            "readOnly",
            "staticToken");

        using var response = await SendGrantRequestAsync(
            fixture.Client,
            token,
            Guid.NewGuid(),
            "read");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fixture.AccessGrantPrompt.RequestCount);
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

    private static async Task<string> CreateAgentAsync(
        HttpClient client,
        string clientId,
        string accessMode,
        string authorizationMode)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/clients")
        {
            Content = JsonContent.Create(
                new HostCreateClientRequest(
                    clientId,
                    accessMode,
                    authorizationMode))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostCreateClientData>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data.Token;
    }

    private static async Task<HttpResponseMessage> SendGrantRequestAsync(
        HttpClient client,
        string agentToken,
        Guid clientInstanceId,
        params string[] scopes)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/mcp/access-grants")
        {
            Content = JsonContent.Create(
                new HostRequestAccessGrantRequest(
                    clientInstanceId,
                    scopes))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            agentToken);
        return await client.SendAsync(request);
    }
}
