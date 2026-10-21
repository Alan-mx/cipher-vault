using System.IO;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Security;

internal static class SecurityLogSafety
{
    public static async Task TryWriteAsync(
        ISecurityEventLog securityLog,
        HostSecurityLogEvent securityEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await securityLog.WriteAsync(securityEvent, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("security_event_log_failed");
        }
    }
}
