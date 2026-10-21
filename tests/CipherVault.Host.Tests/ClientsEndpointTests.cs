using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class ClientsEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ListClientsReturnsPoliciesWithoutTokenHash()
    {
        var policy = new AgentClientPolicy(
            "codex-readonly",
            new byte[32],
            McpAccessMode.ReadOnly,
            AllowLock: false,
            Enabled: true);
        await using var fixture = await HostTestFixture.CreateAsync(
            clients: [policy]);
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<IReadOnlyList<HostClientData>>>(JsonOptions);
        Assert.NotNull(payload);
        var client = Assert.Single(payload.Data);
        Assert.Equal("codex-readonly", client.Id);
        Assert.Equal("readOnly", client.AccessMode);
        Assert.False(client.AllowLock);
        Assert.True(client.Enabled);
        Assert.DoesNotContain(
            "token",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListClientsRequiresAdminToken()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest();

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/clients");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
