using CipherVault.Server.Hosting;
using CipherVault.Server.Middleware;
using Microsoft.AspNetCore.Http;

namespace CipherVault.Server.Tests;

public sealed class ContainerModeHostValidationTests
{
    [Fact]
    public async Task ContainerWildcardBindAcceptsConfiguredPublicPort()
    {
        var options = CreateOptions(
            containerMode: true,
            listenPort: 43127,
            allowedPorts: [43127]);
        var result = await InvokeAsync(
            options,
            new HostString("127.0.0.1", 43127),
            "http://127.0.0.1:43127");

        Assert.True(result.Called);
        Assert.Equal(StatusCodes.Status200OK, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task ContainerModeRejectsArbitraryHostPort()
    {
        var options = CreateOptions(
            containerMode: true,
            listenPort: 43127,
            allowedPorts: [43127]);
        var result = await InvokeAsync(
            options,
            new HostString("127.0.0.1", 54321));

        Assert.False(result.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task ContainerModeRejectsArbitraryOriginPort()
    {
        var options = CreateOptions(
            containerMode: true,
            listenPort: 43127,
            allowedPorts: [43127]);
        var result = await InvokeAsync(
            options,
            new HostString("localhost", 43127),
            "http://localhost:54321");

        Assert.False(result.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task DockerPublicPortIsIndependentFromInternalListenPort()
    {
        var options = CreateOptions(
            containerMode: true,
            listenPort: 8080,
            allowedPorts: [18080]);

        var accepted = await InvokeAsync(
            options,
            new HostString("localhost", 18080),
            "http://localhost:18080");
        var internalPort = await InvokeAsync(
            options,
            new HostString("localhost", 8080));

        Assert.True(accepted.Called);
        Assert.False(internalPort.Called);
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            internalPort.Context.Response.StatusCode);
    }

    [Fact]
    public async Task AllowedPortsDefaultToListenerPorts()
    {
        var options = CreateOptions(
            containerMode: true,
            listenPort: 8080,
            allowedPorts: null);

        var result = await InvokeAsync(
            options,
            new HostString("localhost", 8080),
            "http://localhost:8080");

        Assert.True(result.Called);
        Assert.Equal([8080], options.AllowedHttpPorts);
    }

    [Fact]
    public async Task HostWithoutPortUsesHttpDefaultPort()
    {
        var rejectedOptions = CreateOptions(
            listenPort: 43127,
            allowedPorts: [43127]);
        var rejected = await InvokeAsync(
            rejectedOptions,
            new HostString("localhost"));

        var acceptedOptions = CreateOptions(
            listenPort: 43127,
            allowedPorts: [80]);
        var accepted = await InvokeAsync(
            acceptedOptions,
            new HostString("localhost"),
            "http://localhost");

        Assert.False(rejected.Called);
        Assert.True(accepted.Called);
    }

    [Fact]
    public async Task DevelopmentOriginDoesNotBypassOriginStructure()
    {
        var options = CreateOptions(
            listenPort: 43127,
            allowedPorts: [43127],
            developmentOrigins: [new Uri("http://127.0.0.1:5173")]);

        var accepted = await InvokeAsync(
            options,
            new HostString("127.0.0.1", 43127),
            "http://127.0.0.1:5173");
        var withPath = await InvokeAsync(
            options,
            new HostString("127.0.0.1", 43127),
            "http://127.0.0.1:5173/path");

        Assert.True(accepted.Called);
        Assert.False(withPath.Called);
    }

    [Theory]
    [InlineData("https://localhost:43127")]
    [InlineData("http://user@localhost:43127")]
    [InlineData("http://localhost:43127/path")]
    [InlineData("http://localhost:43127?query=1")]
    [InlineData("http://localhost:43127#fragment")]
    [InlineData("not-an-origin")]
    public async Task InvalidOriginFormsAreRejected(string origin)
    {
        var options = CreateOptions(
            listenPort: 43127,
            allowedPorts: [43127]);
        var result = await InvokeAsync(
            options,
            new HostString("localhost", 43127),
            origin);

        Assert.False(result.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task ForwardedHeadersDoNotChangeHostValidation()
    {
        var options = CreateOptions(
            containerMode: true,
            listenPort: 43127,
            allowedPorts: [43127]);
        var result = await InvokeAsync(
            options,
            new HostString("localhost", 54321),
            origin: null,
            forwardedHost: "localhost:43127");

        Assert.False(result.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task NullOriginIsAllowedOnlyForTicketFormDownload()
    {
        var options = CreateOptions(
            listenPort: 43127,
            allowedPorts: [43127]);
        var ticketDownload = await InvokeAsync(
            options,
            new HostString("localhost", 43127),
            origin: "null",
            method: "POST",
            path: "/api/exports/plaintext/download",
            contentType: "application/x-www-form-urlencoded");
        var ordinaryApi = await InvokeAsync(
            options,
            new HostString("localhost", 43127),
            origin: "null",
            method: "POST",
            path: "/api/documents",
            contentType: "application/x-www-form-urlencoded");

        Assert.True(ticketDownload.Called);
        Assert.False(ordinaryApi.Called);
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ordinaryApi.Context.Response.StatusCode);
    }

    [Fact]
    public async Task TailscaleMagicDnsHostAndOriginAreAccepted()
    {
        var options = CreateOptions(
            listenPort: 43127,
            allowedPorts: [43127],
            allowedHosts:
            [
                "127.0.0.1",
                "localhost",
                "oneplus6.tail8c2fc6.ts.net"
            ]);
        var result = await InvokeAsync(
            options,
            new HostString("oneplus6.tail8c2fc6.ts.net", 43127),
            "http://oneplus6.tail8c2fc6.ts.net:43127");

        Assert.True(result.Called);
        Assert.Equal(StatusCodes.Status200OK, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task TailscaleIPv4HostAndOriginAreAccepted()
    {
        var options = CreateOptions(
            listenPort: 43127,
            allowedPorts: [43127],
            allowedHosts:
            [
                "127.0.0.1",
                "localhost",
                "100.123.123.123"
            ]);
        var result = await InvokeAsync(
            options,
            new HostString("100.123.123.123", 43127),
            "http://100.123.123.123:43127");

        Assert.True(result.Called);
        Assert.Equal(StatusCodes.Status200OK, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task TailscaleIPv6HostAndOriginAreAccepted()
    {
        var options = CreateOptions(
            listenPort: 43127,
            allowedPorts: [43127],
            allowedHosts:
            [
                "127.0.0.1",
                "localhost",
                "fd7a:115c:a1e0::e634:ff1c"
            ]);
        var result = await InvokeAsync(
            options,
            HostString.FromUriComponent(
                "[fd7a:115c:a1e0::e634:ff1c]:43127"),
            "http://[fd7a:115c:a1e0::e634:ff1c]:43127");

        Assert.True(result.Called);
        Assert.Equal(StatusCodes.Status200OK, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task UnconfiguredTailscaleHostIsRejected()
    {
        var options = CreateOptions(
            listenPort: 43127,
            allowedPorts: [43127]);
        var result = await InvokeAsync(
            options,
            new HostString("oneplus6.tail8c2fc6.ts.net", 43127),
            "http://oneplus6.tail8c2fc6.ts.net:43127");

        Assert.False(result.Called);
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            result.Context.Response.StatusCode);
    }

    [Theory]
    [InlineData("localhost.")]
    [InlineData("")]
    [InlineData("localhost:not-a-port")]
    [InlineData("[::1")]
    public async Task MalformedHostIsRejected(string host)
    {
        var options = CreateOptions(
            listenPort: 43127,
            allowedPorts: [43127]);
        var result = await InvokeAsync(
            options,
            new HostString(host, 43127));

        Assert.False(result.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
    }

    private static ServerOptions CreateOptions(
        bool containerMode = false,
        int listenPort = 43127,
        IReadOnlyList<int>? allowedPorts = null,
        IReadOnlyList<Uri>? developmentOrigins = null,
        IReadOnlyList<string>? allowedHosts = null)
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-container-host-{Guid.NewGuid():N}");
        return new ServerOptions(
            ApplicationPaths.Create(
                Path.Combine(parent, "data"),
                Path.Combine(parent, "config")),
            [new ServerListenAddress(
                containerMode ? "0.0.0.0" : "127.0.0.1",
                listenPort)],
            TimeSpan.FromMinutes(10),
            ServerOptions.DefaultMaxRequestBodyBytes,
            containerMode,
            allowedHosts: allowedHosts ?? ["127.0.0.1", "localhost"],
            developmentOrigins: developmentOrigins,
            allowedHttpPorts: allowedPorts);
    }

    private static async Task<InvocationResult> InvokeAsync(
        ServerOptions options,
        HostString host,
        string? origin = null,
        string? forwardedHost = null,
        string method = "GET",
        string path = "/",
        string? contentType = null)
    {
        var called = false;
        var middleware = new HostOriginValidationMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            options);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = host;
        if (contentType is not null)
        {
            context.Request.ContentType = contentType;
        }
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }
        if (forwardedHost is not null)
        {
            context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
            context.Request.Headers["X-Forwarded-Proto"] = "http";
            context.Request.Headers["X-Forwarded-Port"] = "43127";
        }

        await middleware.InvokeAsync(context);
        return new InvocationResult(context, called);
    }

    private sealed record InvocationResult(
        DefaultHttpContext Context,
        bool Called);
}
