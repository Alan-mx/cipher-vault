using System.Security.Principal;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Mcp;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;
using Microsoft.Extensions.Hosting;

namespace CipherVault.Host.Service;

public sealed class StrictBridgeHostedService(
    CipherVaultServiceConfigFile configuration,
    CipherVaultServicePaths paths,
    StrictServiceHostOptions options,
    HostSessionCoordinator sessions,
    AccessGrantService grants,
    IHostAccessGrantPrompt grantPrompt,
    StrictAdminVaultRpcProvider rpc,
    HostVaultGateway vaultGateway,
    OwnerSessionRegistry ownerSessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableBridgePipe)
        {
            return;
        }
        var bridge = configuration.Agent?.Bridges?.FirstOrDefault(item =>
            item.Enabled != false &&
            string.Equals(item.Id, "mcp", StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid("Strict MCP bridge policy is missing.");
        var install = CipherVaultServiceConfiguration.LoadInstallManifest(
            paths.PublicManifestPath);
        var serviceSid = install.ServiceSid
            ?? throw Invalid("Service SID is required.");
        var ownerSid = configuration.OwnerSid
            ?? throw Invalid("Owner SID is required.");
        var servicePolicy = AdminPipeHostedService.CreateServicePolicy(
            configuration,
            serviceSid,
            paths.InstallRoot);
        var verification = WindowsProcessIdentityVerifier.Verify(
            (uint)Environment.ProcessId,
            servicePolicy);
        if (verification.Status != ProcessVerificationStatus.Verified ||
            verification.Evidence is null)
        {
            verification.Evidence?.Dispose();
            throw Invalid("Host service identity verification failed.");
        }
        using var evidence = verification.Evidence;
        var bridgePolicy = AdminPipeHostedService.CreateBridgePolicy(
            configuration,
            ownerSid,
            paths.InstallRoot);
        var security = PipeSecurityFactory.Create(
            [new SecurityIdentifier(serviceSid)],
            [new SecurityIdentifier(ownerSid)]);
        var handler = new StrictBridgeHandler(
            sessions,
            grants,
            grantPrompt,
            rpc,
            vaultGateway);
        var serverOptions = new BridgePipeServerOptions
        {
            PipeName = configuration.Pipes?.Bridge
                ?? throw Invalid("Bridge pipe is required."),
            Security = security,
            ServiceInstanceId = Guid.NewGuid().ToString("D"),
            ServiceVersion = typeof(StrictBridgeHostedService).Assembly
                .GetName().Version?.ToString() ?? "1.0.0",
            ServiceIdentity = evidence.Identity,
            AllowedClientPolicy = bridgePolicy,
            OwnerSid = ownerSid,
            ServiceSid = serviceSid,
            ClientId = bridge.Id ?? "mcp",
            AccessMode = string.Equals(
                bridge.AccessMode,
                "readWrite",
                StringComparison.OrdinalIgnoreCase)
                ? McpAccessMode.ReadWrite
                : McpAccessMode.ReadOnly,
            BridgeSessionLifetime = TimeSpan.FromHours(12),
            Handler = handler,
            OwnerSessions = ownerSessions
        };
        await new BridgePipeAcceptLoop(serverOptions)
            .RunAsync(stoppingToken).ConfigureAwait(false);
    }

    private static VaultException Invalid(string message)
    {
        return new VaultException(VaultErrorCode.InvalidInput, message);
    }
}
