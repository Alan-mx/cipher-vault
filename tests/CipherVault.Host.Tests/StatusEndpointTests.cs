using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class StatusEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ValidAdminTokenReturnsHostStatus()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = HostTestFixture.CreateRequest("/admin/v1/status");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostStatusData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.True(payload.Ok);
        Assert.Equal("ready", payload.Data.Status);
        Assert.Equal(HostTestFixture.BaseUrl, payload.Data.Endpoint.BaseUrl);
        Assert.Equal("/mcp", payload.Data.Endpoint.McpPath);
        Assert.Equal("/admin/v1", payload.Data.Endpoint.AdminPath);
        Assert.Equal("locked", payload.Data.Session.State);
        Assert.Equal(0, payload.Data.Session.ActiveLeases);
        Assert.Equal(0, payload.Data.Session.ConnectedClients);
    }

    [Fact]
    public async Task StatusRequiresAdminToken()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = HostTestFixture.CreateRequest("/admin/v1/status");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
