using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class HealthEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ValidAdminTokenReturnsHealth()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = HostTestFixture.CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out var requestIds));
        Assert.Single(requestIds);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostHealthData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.True(payload.Ok);
        Assert.Equal("ready", payload.Data.Status);
        Assert.Equal("1", payload.Data.ProtocolVersion);
        Assert.NotEqual(Guid.Empty, Guid.Parse(payload.Data.InstanceId));
        Assert.Equal(64, payload.Data.ConfigFingerprint.Length);
    }

    [Fact]
    public async Task MissingTokenIsRejected()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = HostTestFixture.CreateRequest();

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.False(payload.Ok);
        Assert.Equal("invalid_token", payload.Error.Code);
    }

    [Fact]
    public async Task InvalidTokenIsRejected()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = HostTestFixture.CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "wrong-token");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_token", payload.Error.Code);
    }

    [Fact]
    public async Task InvalidHostHeaderIsRejectedBeforeAuthentication()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = HostTestFixture.CreateRequest();
        request.Headers.Host = "evil.example";

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_host", payload.Error.Code);
    }

    [Fact]
    public async Task InvalidOriginIsRejectedAndAllowedOriginPasses()
    {
        await using var fixture = await HostTestFixture.CreateAsync(
            allowedOrigins: ["http://localhost:3000"]);
        using var rejected = HostTestFixture.CreateRequest();
        rejected.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        rejected.Headers.TryAddWithoutValidation(
            "Origin",
            "https://evil.example");

        using var rejectedResponse = await fixture.Client.SendAsync(rejected);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedResponse.StatusCode);
        var error = await rejectedResponse.Content.ReadFromJsonAsync<
            HostApiErrorResponse>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_origin", error.Error.Code);

        using var allowed = HostTestFixture.CreateRequest();
        allowed.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        allowed.Headers.TryAddWithoutValidation(
            "Origin",
            "http://localhost:3000");
        using var allowedResponse = await fixture.Client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
    }

    [Fact]
    public async Task NativeRequestWithoutOriginIsAccepted()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = HostTestFixture.CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
