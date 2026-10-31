namespace CipherVault.Server.Tests;

public sealed class ServerHostSmokeTests
{
    [Fact]
    public async Task StaticPlaceholderAndJsonApiNotFoundAreServed()
    {
        await using var server = await ServerTestApplication.CreateAsync();

        using var home = await server.Client.GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.OK, home.StatusCode);
        Assert.Contains(
            "CipherVault",
            await home.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var deepLink = await server.Client.GetAsync("/documents/example");
        Assert.Equal(System.Net.HttpStatusCode.OK, deepLink.StatusCode);
        Assert.Contains(
            "CipherVault",
            await deepLink.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var missingAsset = await server.Client.GetAsync(
            "/assets/missing.js");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missingAsset.StatusCode);

        using var missing = await server.Client.GetAsync("/api/missing");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "no-store",
            missing.Headers.CacheControl?.ToString());
        Assert.True(missing.Headers.Contains("X-Correlation-ID"));
        Assert.Contains(
            "\"code\":\"route_not_found\"",
            await missing.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var wrongMethod = await server.Client.PostAsync(
            "/api/vault/status",
            content: null);
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Contains(
            "\"code\":\"method_not_allowed\"",
            await wrongMethod.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }
}
