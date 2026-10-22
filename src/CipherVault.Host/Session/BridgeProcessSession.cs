using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Security.Win;

namespace CipherVault.Host.Session;

public sealed record BridgeProcessSession(
    Guid ProcessSessionId,
    Guid BridgeInstanceId,
    VerifiedProcessIdentity Client,
    string OwnerSid,
    string ServiceSid,
    string ClientId,
    McpAccessMode AccessMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
