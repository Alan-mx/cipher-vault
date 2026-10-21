using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace CipherVault.Host.Tests;

public sealed class HealthEndpointTests
{
    private const string AdminToken = "test-admin-token";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ValidAdminTokenReturnsHealth()
    {
        await using var fixture = await HostFixture.CreateAsync();
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AdminToken);

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
    }

    [Fact]
    public async Task MissingTokenIsRejected()
    {
        await using var fixture = await HostFixture.CreateAsync();
        using var request = CreateRequest();

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
        await using var fixture = await HostFixture.CreateAsync();
        using var request = CreateRequest();
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
        await using var fixture = await HostFixture.CreateAsync();
        using var request = CreateRequest();
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
        await using var fixture = await HostFixture.CreateAsync(
            allowedOrigins: ["http://localhost:3000"]);
        using var rejected = CreateRequest();
        rejected.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AdminToken);
        rejected.Headers.TryAddWithoutValidation(
            "Origin",
            "https://evil.example");

        using var rejectedResponse = await fixture.Client.SendAsync(rejected);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedResponse.StatusCode);
        var error = await rejectedResponse.Content.ReadFromJsonAsync<
            HostApiErrorResponse>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_origin", error.Error.Code);

        using var allowed = CreateRequest();
        allowed.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AdminToken);
        allowed.Headers.TryAddWithoutValidation(
            "Origin",
            "http://localhost:3000");
        using var allowedResponse = await fixture.Client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
    }

    [Fact]
    public async Task NativeRequestWithoutOriginIsAccepted()
    {
        await using var fixture = await HostFixture.CreateAsync();
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/v1/health");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private HostFixture(WebApplication app, HttpClient client)
        {
            App = app;
            Client = client;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }

        public static async Task<HostFixture> CreateAsync(
            string[]? allowedOrigins = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var configuration = new ResolvedCipherVaultHostConfig(
                Path.Combine(Path.GetTempPath(), "cipher-vault-host-test"),
                VaultSessionOptions.Default,
                new HostEndpointOptions(
                    "127.0.0.1",
                    43127,
                    "/mcp",
                    "/admin/v1",
                    4096,
                    new HashSet<string>(
                        allowedOrigins ?? [],
                        StringComparer.OrdinalIgnoreCase)),
                [],
                Path.Combine(Path.GetTempPath(), "config.json"));
            var app = HostApplication.Configure(
                builder,
                configuration,
                new TestAdminTokenStore(AdminToken));
            await app.StartAsync();
            return new HostFixture(app, app.GetTestClient());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class TestAdminTokenStore(string token) : IAdminTokenStore
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(token);
        }
    }
}
