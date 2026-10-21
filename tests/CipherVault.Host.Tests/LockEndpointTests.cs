using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class LockEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task LockEndpointLocksUnlockedSession()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await SendUnlockAsync(fixture.Client);
        using var request = CreateLockRequest("test-lock");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostSessionStatusData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("locked", payload.Data.State);
        Assert.Equal("test-lock", payload.Data.LockReason);
    }

    [Fact]
    public async Task LockEndpointIsIdempotentWhenAlreadyLocked()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateLockRequest("already-locked");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostSessionStatusData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("locked", payload.Data.State);
    }

    private static async Task SendUnlockAsync(HttpClient client)
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

    private static HttpRequestMessage CreateLockRequest(string reason)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/session/lock")
        {
            Content = JsonContent.Create(new HostLockRequest(reason))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        return request;
    }
}
