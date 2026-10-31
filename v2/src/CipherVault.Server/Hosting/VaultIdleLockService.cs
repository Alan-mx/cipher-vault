using CipherVault.Application.Vault;
using CipherVault.Core.Errors;

namespace CipherVault.Server.Hosting;

public sealed class VaultIdleLockService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<VaultIdleLockService> logger;

    public VaultIdleLockService(
        VaultSessionManager sessionManager,
        TimeProvider timeProvider,
        ILogger<VaultIdleLockService> logger)
    {
        this.sessionManager = sessionManager;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _ = await sessionManager.CheckIdleAndLockAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (VaultException exception)
                {
                    logger.LogWarning(
                        new EventId(3000, "IdleLockFailed"),
                        "Idle lock check failed with {ErrorCode}",
                        exception.Code);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            var status = await sessionManager.GetStatusAsync(CancellationToken.None);
            if (status.State is not (
                    VaultSessionState.Uninitialized
                    or VaultSessionState.Locked))
            {
                _ = await sessionManager.LockAsync(CancellationToken.None);
            }
        }
        catch (VaultException exception)
        {
            logger.LogWarning(
                new EventId(3001, "ShutdownLockFailed"),
                "Shutdown lock failed with {ErrorCode}",
                exception.Code);
        }
    }
}
