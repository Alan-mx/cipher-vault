using CipherVault.Core.Configuration;
using CipherVault.Core.Session;
using CipherVault.Host.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace CipherVault.Host.Tests;

internal sealed class HostTestFixture : IAsyncDisposable
{
    public const string AdminToken = "test-admin-token";
    public const string BaseUrl = "http://127.0.0.1:43127";

    private HostTestFixture(WebApplication app, HttpClient client)
    {
        App = app;
        Client = client;
    }

    public WebApplication App { get; }
    public HttpClient Client { get; }

    public static async Task<HostTestFixture> CreateAsync(
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
        return new HostTestFixture(app, app.GetTestClient());
    }

    public static HttpRequestMessage CreateRequest(
        string path = "/admin/v1/health")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }

    private sealed class TestAdminTokenStore(string token) : IAdminTokenStore
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(token);
        }
    }
}
