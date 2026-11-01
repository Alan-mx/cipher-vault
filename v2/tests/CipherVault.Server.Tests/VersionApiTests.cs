using System.Net;
using System.Text.Json;
using CipherVault.Server.Build;

namespace CipherVault.Server.Tests;

public sealed class VersionApiTests
{
    [Fact]
    public async Task VersionEndpointNeedsNoCookieOrUnlock()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);

        using var response = await server.CreateClient()
            .GetAsync("/api/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "0.0.0-local",
            json.RootElement.GetProperty("productVersion").GetString());
        Assert.Equal(
            "local",
            json.RootElement.GetProperty("commit").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            json.RootElement.GetProperty("rid").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            json.RootElement.GetProperty("runtimeVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            json.RootElement.GetProperty("aspNetCoreVersion").GetString()));
        Assert.False(
            json.RootElement.TryGetProperty("dataPath", out _));
        Assert.False(
            json.RootElement.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task VersionEndpointDoesNotRefreshVaultActivity()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var before = await server.Manager.GetStatusAsync();

        using var response = await server.Client.GetAsync("/api/version");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await server.Manager.GetStatusAsync();
        Assert.Equal(before.LastActivityAtUtc, after.LastActivityAtUtc);
        Assert.Equal(
            before.IdleRemainingSeconds,
            after.IdleRemainingSeconds);
    }

    [Fact]
    public void LocalBuildMetadataIsExplicitlyNonRelease()
    {
        var metadata = BuildMetadataReader.Read();

        Assert.Equal("0.0.0-local", metadata.ProductVersion);
        Assert.Equal("local", metadata.Commit);
        Assert.False(metadata.IsRelease);
        Assert.False(string.IsNullOrWhiteSpace(metadata.Rid));
        Assert.False(string.IsNullOrWhiteSpace(metadata.RuntimeVersion));
    }
}
