using CipherVault.Application.Backup;
using CipherVault.Application.Vault;
using CipherVault.Server.Hosting;
using CipherVault.Server.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CipherVault.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var commandLine = ServerCommandLine.Parse(args);
        if (commandLine.Mode == ServerRunMode.Invalid)
        {
            Console.Error.WriteLine(commandLine.ErrorCode);
            return RestoreExitCodes.ArgumentsInvalid;
        }
        if (commandLine.Mode == ServerRunMode.Restore)
        {
            return await RunRestoreAsync(
                commandLine.RestoreOptions
                    ?? throw new InvalidOperationException(
                        "Restore options are missing."));
        }

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
            var backupStorageOptions = new BackupStorageOptions(
                options.Paths.BackupDirectory,
                options.Paths.BackupLockPath);
            await new BackupStartupCleanupService(backupStorageOptions)
                .InitializeAsync();
            var timeProvider = TimeProvider.System;
            var tokenStore = await McpTokenStore.LoadAsync(
                options.Paths.McpTokenPath,
                timeProvider);
            var manager = await VaultSessionManager.CreateAsync(
                options.Paths.VaultDirectory,
                new VaultSessionOptions(options.IdleTimeout),
                timeProvider);
            try
            {
                await using var app = await ServerWebApplication.BuildAsync(
                    options,
                    manager,
                    tokenStore,
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

    private static async Task<int> RunRestoreAsync(
        RestoreCommandOptions restoreOptions)
    {
        var terminal = new InteractiveConsoleTerminal();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();
            var options = ServerOptions.Load(configuration);
            return await new RestoreMaintenanceCommand().RunAsync(
                options,
                restoreOptions,
                terminal,
                cancellation.Token);
        }
        catch (ServerStartupException)
        {
            terminal.WriteSafeError(
                new RestoreOutcome(
                    Succeeded: false,
                    ExitCode: RestoreExitCodes.TargetInvalid,
                    FailureCode: RestoreFailureCode.TargetInvalid,
                    SafeMessage: "restore_target_invalid"));
            return RestoreExitCodes.TargetInvalid;
        }
        catch (OperationCanceledException)
        {
            terminal.WriteSafeError(
                new RestoreOutcome(
                    Succeeded: false,
                    ExitCode: RestoreExitCodes.PublishFailed,
                    FailureCode: RestoreFailureCode.Cancelled,
                    SafeMessage: "restore_cancelled"));
            return RestoreExitCodes.PublishFailed;
        }
        catch
        {
            terminal.WriteSafeError(
                new RestoreOutcome(
                    Succeeded: false,
                    ExitCode: RestoreExitCodes.PublishFailed,
                    FailureCode: RestoreFailureCode.PublishFailed,
                    SafeMessage: "restore_publish_failed"));
            return RestoreExitCodes.PublishFailed;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
