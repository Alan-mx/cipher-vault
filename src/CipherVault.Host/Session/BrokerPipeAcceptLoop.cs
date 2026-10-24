using System.ComponentModel;
using CipherVault.Core.Session;
using CipherVault.Host.Operations;
using CipherVault.Host.Security;
using Microsoft.Extensions.Logging;

namespace CipherVault.Host.Session;

public sealed class BrokerPipeAcceptLoop(
    BrokerPipeServerOptions options,
    BrokerConnectionRegistry registry,
    BrokerActivityProvider activityProvider,
    IBrokerLifecycleLog lifecycleLog,
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
        ArgumentNullException.ThrowIfNull(lifecycleLog);
        ArgumentNullException.ThrowIfNull(logger);

        var failureCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            BrokerPipeServer? server = null;
            CancellationTokenSource? connectionWatch = null;
            Task? monitorTask = null;
            BrokerPipeConnection? acceptedConnection = null;
            var stage = "create";
            try
            {
                server = new BrokerPipeServer(loopOptions);
                lifecycleLog.Write(Event(
                    "pipe_created",
                    stage,
                    "success"));
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
                        acceptedConnection = connection;
                        registry.Set(connection);
                        failureCount = 0;
                        accepted.TrySetResult();
                        lifecycleLog.Write(Event(
                            "broker_accepted",
                            "session",
                            "success",
                            connection));
                        logger.LogInformation(
                            "Auth Broker pipe accepted process {ProcessId}.",
                            connection.ClientIdentity.ProcessId);
                    },
                    connection =>
                    {
                        registry.Clear(connection);
                        activityProvider.Clear();
                        lifecycleLog.Write(Event(
                            "broker_released",
                            "session",
                            "success",
                            connection));
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
                lifecycleLog.Write(Event(
                    "broker_duplicate_rejected",
                    stage,
                    "rejected",
                    retryCount: failureCount,
                    errorType: GetErrorType(exception),
                    message: exception.Message));
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
                lifecycleLog.Write(Event(
                    "session_cancelled",
                    stage,
                    "cancelled",
                    acceptedConnection,
                    message: "Connection watch requested pipe recycling."));
                failureCount = 0;
            }
            catch (Exception exception)
            {
                failureCount++;
                lifecycleLog.Write(Event(
                    "pipe_failed",
                    stage,
                    "error",
                    acceptedConnection,
                    retryCount: failureCount,
                    errorType: GetErrorType(exception),
                    win32ErrorCode: GetWin32ErrorCode(exception),
                    message: exception.Message));
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
                if (acceptedConnection is not null &&
                    !registry.IsAvailable)
                {
                    lifecycleLog.Write(Event(
                        "broker_unavailable",
                        "cleanup",
                        "unavailable",
                        acceptedConnection));
                }
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
                        lifecycleLog.Write(Event(
                            "monitor_cleanup_failed",
                            "cleanup",
                            "error",
                            acceptedConnection,
                            errorType: GetErrorType(exception),
                            win32ErrorCode: GetWin32ErrorCode(exception),
                            message: exception.Message));
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
                        lifecycleLog.Write(Event(
                            "pipe_disposed",
                            "cleanup",
                            "success",
                            acceptedConnection));
                    }
                    catch (Exception exception)
                    {
                        lifecycleLog.Write(Event(
                            "pipe_disposal_failed",
                            "cleanup",
                            "error",
                            acceptedConnection,
                            errorType: GetErrorType(exception),
                            win32ErrorCode: GetWin32ErrorCode(exception),
                            message: exception.Message));
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

    private static BrokerLifecycleLogEvent Event(
        string eventType,
        string stage,
        string result,
        BrokerPipeConnection? connection = null,
        int? retryCount = null,
        string? errorType = null,
        int? win32ErrorCode = null,
        string? message = null)
    {
        return new BrokerLifecycleLogEvent(
            DateTimeOffset.UtcNow,
            eventType,
            stage,
            result,
            connection?.ClientIdentity.ProcessId,
            connection?.ClientIdentity.ProcessStartedAt,
            retryCount,
            errorType,
            win32ErrorCode,
            message);
    }

    private static string GetErrorType(Exception exception)
    {
        return exception.GetType().Name;
    }

    private static int? GetWin32ErrorCode(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is Win32Exception win32Exception)
            {
                return win32Exception.NativeErrorCode;
            }
        }
        return null;
    }
}
