using CipherVault.Server.Hosting;
using CipherVault.Server.Middleware;
using Microsoft.AspNetCore.Http;

namespace CipherVault.Server.Tests;

public sealed class ContainerModeHostValidationTests
{
    [Fact]
    public async Task ContainerWildcardBindAcceptsLoopbackHostAndOrigin()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-container-host-{Guid.NewGuid():N}");
        var options = new ServerOptions(
            ApplicationPaths.Create(
                Path.Combine(parent, "data"),
                Path.Combine(parent, "config")),
            [new ServerListenAddress("0.0.0.0", 43127)],
            TimeSpan.FromMinutes(10),
            ServerOptions.DefaultMaxRequestBodyBytes,
            containerMode: true,
            allowedHosts: ["127.0.0.1", "localhost"]);
        var called = false;
        var middleware = new HostOriginValidationMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            options);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("127.0.0.1", 54321);
        context.Request.Headers.Origin = "http://127.0.0.1:54321";

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task ContainerModeStillRejectsNonLoopbackHost()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-container-host-{Guid.NewGuid():N}");
        var options = new ServerOptions(
            ApplicationPaths.Create(
                Path.Combine(parent, "data"),
                Path.Combine(parent, "config")),
            [new ServerListenAddress("0.0.0.0", 43127)],
            TimeSpan.FromMinutes(10),
            ServerOptions.DefaultMaxRequestBodyBytes,
            containerMode: true);
        var middleware = new HostOriginValidationMiddleware(
            _ => throw new InvalidOperationException("Must not continue."),
            options);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("example.com", 43127);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExplicitDevelopmentOriginIsAllowed()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-development-origin-{Guid.NewGuid():N}");
        var options = new ServerOptions(
            ApplicationPaths.Create(
                Path.Combine(parent, "data"),
                Path.Combine(parent, "config")),
            [new ServerListenAddress("127.0.0.1", 43127)],
            TimeSpan.FromMinutes(10),
            ServerOptions.DefaultMaxRequestBodyBytes,
            developmentOrigins: [new Uri("http://127.0.0.1:5173")]);
        var called = false;
        var middleware = new HostOriginValidationMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            options);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("127.0.0.1", 43127);
        context.Request.Headers.Origin = "http://127.0.0.1:5173";

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Fact]
    public async Task UnlistedDevelopmentOriginIsRejected()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-development-origin-{Guid.NewGuid():N}");
        var options = new ServerOptions(
            ApplicationPaths.Create(
                Path.Combine(parent, "data"),
                Path.Combine(parent, "config")),
            [new ServerListenAddress("127.0.0.1", 43127)],
            TimeSpan.FromMinutes(10),
            ServerOptions.DefaultMaxRequestBodyBytes,
            developmentOrigins: [new Uri("http://127.0.0.1:5173")]);
        var middleware = new HostOriginValidationMiddleware(
            _ => throw new InvalidOperationException("Must not continue."),
            options);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("127.0.0.1", 43127);
        context.Request.Headers.Origin = "http://127.0.0.1:5174";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }
}
