using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Host.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CipherVault.Host.Tests;

public sealed class StopEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ValidAdminTokenLocksAndStopsHost()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest(new HostStopRequest("acceptance-stop"));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!fixture.Lifetime.ApplicationStopping.IsCancellationRequested &&
               DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        Assert.True(fixture.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task StopRequiresAdminTokenAndDoesNotStopHost()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/host/stop")
        {
            Content = JsonContent.Create(new HostStopRequest("unauthorized"))
        };
        request.Headers.Host = "127.0.0.1:43127";

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(fixture.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task OverlongStopReasonIsRejected()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest(new HostStopRequest(new string('a', 201)));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_input", payload.Error.Code);
        Assert.False(fixture.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    private static HttpRequestMessage CreateRequest(HostStopRequest requestBody)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/host/stop")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        return request;
    }
}
