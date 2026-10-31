using CipherVault.Application.Vault;
using CipherVault.Server.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CipherVault.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        _ = args;
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
            });
            builder.SetMinimumLevel(LogLevel.Warning);
        });
        var logger = loggerFactory.CreateLogger("CipherVault.Server.Startup");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();
            var options = ServerOptions.Load(configuration);
            options.Paths.EnsureCreated();
            await using var instanceLock = ServerInstanceLock.Acquire(
                options.Paths.ServerLockPath);
            var timeProvider = TimeProvider.System;
            var manager = await VaultSessionManager.CreateAsync(
                options.Paths.VaultDirectory,
                new VaultSessionOptions(options.IdleTimeout),
                timeProvider);
            try
            {
                await using var app = await ServerWebApplication.BuildAsync(
                    options,
                    manager,
                    timeProvider);
                await app.WaitForShutdownAsync();
            }
            catch
            {
                await manager.DisposeAsync();
                throw;
            }
            return 0;
        }
        catch (ServerStartupException exception)
        {
            logger.LogCritical(
                new EventId(4000, "ServerStartupFailed"),
                "CipherVault.Server startup failed with {ErrorCode}",
                exception.Code);
            return 1;
        }
        catch
        {
            logger.LogCritical(
                new EventId(4001, "ServerStartupFailed"),
                "CipherVault.Server startup failed with {ErrorCode}",
                "InternalError");
            return 1;
        }
    }
}
