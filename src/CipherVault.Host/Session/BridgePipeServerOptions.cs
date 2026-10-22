using System.IO.Pipes;
using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Security.Win;

namespace CipherVault.Host.Session;

public sealed record BridgePipeServerOptions
{
    public required string PipeName { get; init; }
    public required PipeSecurity Security { get; init; }
    public required string ServiceInstanceId { get; init; }
    public required string ServiceVersion { get; init; }
    public required VerifiedProcessIdentity ServiceIdentity { get; init; }
    public required ProcessVerificationPolicy AllowedClientPolicy { get; init; }
    public required string OwnerSid { get; init; }
    public required string ServiceSid { get; init; }
    public required string ClientId { get; init; }
    public required McpAccessMode AccessMode { get; init; }
    public required TimeSpan BridgeSessionLifetime { get; init; }
    public required IBridgeSessionHandler Handler { get; init; }
}
