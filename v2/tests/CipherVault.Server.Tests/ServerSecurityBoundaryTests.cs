using System.Net;
using System.Text;
using System.Text.Json;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using Microsoft.Extensions.Logging;

namespace CipherVault.Server.Tests;

public sealed class ServerSecurityBoundaryTests
{
    [Fact]
    public async Task HostAndOriginAreValidatedAndSecurityHeadersAreSet()
    {
        await using var server = await ServerTestApplication.CreateAsync();

        using var hostRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/vault/status");
        hostRequest.Headers.Host = "example.com";
        using var hostResponse = await server.Client.SendAsync(hostRequest);
        Assert.Equal(HttpStatusCode.Forbidden, hostResponse.StatusCode);
        Assert.Contains(
            "host_invalid",
            await hostResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var originRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/vault/status");
        originRequest.Headers.Add("Origin", "http://example.com");
        using var originResponse = await server.Client.SendAsync(originRequest);
        Assert.Equal(HttpStatusCode.Forbidden, originResponse.StatusCode);
        Assert.Contains(
            "origin_invalid",
            await originResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var safe = await server.Client.GetAsync("/api/vault/status");
        Assert.Equal("nosniff", safe.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", safe.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-store", safe.Headers.CacheControl?.ToString());
        var csp = safe.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains(
            "style-src 'self' 'unsafe-inline'",
            csp,
            StringComparison.Ordinal);
        Assert.Contains("img-src 'self' data:", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestBodyOverFourMebibytesReturnsJson413()
    {
        await using var server = await ServerTestApplication.CreateAsync();
        using var content = new StringContent(
            new string('x', checked((4 * 1024 * 1024) + 1)),
            Encoding.UTF8,
            "application/json");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/vault/setup")
        {
            Content = content
        };
        request.Headers.ExpectContinue = true;
        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "request_too_large",
            json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task IdleTimeoutLocksInBackground()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true,
            idleTimeout: TimeSpan.FromMinutes(1));
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        server.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await WaitForLockedAsync(server.Manager);

        Assert.Equal(VaultSessionState.Locked, server.Manager.State);
        using var afterLock = await server.Client.GetAsync(
            "/api/vault/antiforgery");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLock.StatusCode);
    }

    [Fact]
    public async Task ReUnlockInvalidatesCookieFromPreviousSession()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var firstClient = server.CreateClient();
        using var firstUnlock = await firstClient.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, firstUnlock.StatusCode);

        using var secondClient = server.CreateClient();
        using var lockRequest = await firstClient.GetAsync(
            "/api/vault/antiforgery");
        Assert.Equal(HttpStatusCode.OK, lockRequest.StatusCode);
        using var tokenJson = JsonDocument.Parse(
            await lockRequest.Content.ReadAsStringAsync());
        using var locked = await firstClient.PostWithAntiforgeryAsync(
            "/api/vault/lock",
            tokenJson.RootElement.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);

        using var secondUnlock = await secondClient.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, secondUnlock.StatusCode);

        using var stale = await firstClient.GetAsync(
            "/api/vault/antiforgery");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [Fact]
    public async Task BackupRouteRequiresAuthentication()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var anonymous = await server.CreateClient()
            .GetAsync("/api/backups");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task LogsDoNotContainPasswordQueryOrDocumentBody()
    {
        using var logs = new CapturingLoggerProvider();
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true,
            configureLogging: logging => logging.AddProvider(logs));
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        const string bodyMarker = "body-secret-marker";
        using var created = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                "Logs",
                null,
                null,
                VaultDocumentFormat.Text,
                bodyMarker,
                null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var search = await server.Client.GetAsync(
            "/api/search?query=query-secret-marker");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        var logText = string.Join('\n', logs.Messages);
        Assert.DoesNotContain(
            ServerTestApplication.Password,
            logText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "query-secret-marker",
            logText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(bodyMarker, logText, StringComparison.Ordinal);
    }

    private static async Task WaitForLockedAsync(VaultSessionManager manager)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (manager.State == VaultSessionState.Locked)
            {
                return;
            }
            await Task.Delay(20);
        }

        Assert.Equal(VaultSessionState.Locked, manager.State);
    }
}
