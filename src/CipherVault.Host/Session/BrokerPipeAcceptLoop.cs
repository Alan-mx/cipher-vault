using CipherVault.Core.Session;
using CipherVault.Host.Security;
using Microsoft.Extensions.Logging;

namespace CipherVault.Host.Session;

public sealed class BrokerPipeAcceptLoop(
    BrokerPipeServerOptions options,
    BrokerConnectionRegistry registry,
    BrokerActivityProvider activityProvider,
    ILogger<BrokerPipeAcceptLoop> logger)
{
    private static readonly TimeSpan[] FailureBackoffs =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1)
    ];

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var loopOptions = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(activityProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var failureCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            BrokerPipeServer? server = null;
            CancellationTokenSource? connectionWatch = null;
            Task? monitorTask = null;
            var stage = "create";
            try
            {
                server = new BrokerPipeServer(loopOptions);
                connectionWatch =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        stoppingToken);
                var accepted = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                monitorTask = MonitorConnectedBrokerAsync(
                    registry,
                    () => accepted.Task.IsCompleted,
                    connectionWatch,
                    logger);
                stage = "accept";
                await server.AcceptOnceAsync(
                    connection =>
                    {
                        registry.Set(connection);
                        failureCount = 0;
                        accepted.TrySetResult();
                        logger.LogInformation(
                            "Auth Broker pipe accepted process {ProcessId}.",
                            connection.ClientIdentity.ProcessId);
                    },
                    connection =>
                    {
                        registry.Clear(connection);
                        activityProvider.Clear();
                        logger.LogInformation(
                            "Auth Broker pipe released process {ProcessId}.",
                            connection.ClientIdentity.ProcessId);
                    },
                    connectionWatch.Token).ConfigureAwait(false);
            }
            catch (AdminPipeRpcException exception) when (
                string.Equals(
                    exception.Code,
                    "broker_already_connected",
                    StringComparison.Ordinal))
            {
                failureCount++;
                logger.LogWarning(
                    exception,
                    "Auth Broker pipe rejected a duplicate connection.");
                if (!await DelayBeforeRetryAsync(
                        failureCount,
                        stoppingToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (
                connectionWatch?.IsCancellationRequested == true)
            {
                failureCount = 0;
            }
            catch (Exception exception)
            {
                failureCount++;
                logger.LogError(
                    exception,
                    "Auth Broker pipe failed during {Stage}; retry {RetryCount}.",
                    stage,
                    failureCount);
                if (!await DelayBeforeRetryAsync(
                        failureCount,
                        stoppingToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            finally
            {
                connectionWatch?.Cancel();
                if (monitorTask is not null)
                {
                    try
                    {
                        await monitorTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Auth Broker pipe monitor failed during cleanup.");
                    }
                }
                if (server is not null)
                {
                    try
                    {
                        await server.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Auth Broker pipe disposal failed; the next accept attempt will retry.");
                    }
                }
                connectionWatch?.Dispose();
            }
        }
    }

    private static async Task MonitorConnectedBrokerAsync(
        BrokerConnectionRegistry registry,
        Func<bool> hasAccepted,
        CancellationTokenSource connectionWatch,
        ILogger<BrokerPipeAcceptLoop> logger)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(connectionWatch.Token)
                   .ConfigureAwait(false))
        {
            if (!hasAccepted() || registry.IsAvailable)
            {
                continue;
            }
            logger.LogWarning(
                "Auth Broker connection became unavailable; recycling pipe.");
            connectionWatch.Cancel();
            return;
        }
    }

    private static async Task<bool> DelayBeforeRetryAsync(
        int failureCount,
        CancellationToken stoppingToken)
    {
        var index = Math.Min(failureCount - 1, FailureBackoffs.Length - 1);
        try
        {
            await Task.Delay(FailureBackoffs[index], stoppingToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
