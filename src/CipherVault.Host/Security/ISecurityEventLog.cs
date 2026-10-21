using CipherVault.Host.Contracts;

namespace CipherVault.Host.Security;

public interface ISecurityEventLog
{
    Task WriteAsync(
        HostSecurityLogEvent securityEvent,
        CancellationToken cancellationToken = default);
}
