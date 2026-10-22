using System.Diagnostics;
using System.Security.Principal;
using CipherVault.Core.Configuration;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;

namespace CipherVault.Host.Tests;

public sealed class StrictBridgePipeTests
{
    [Fact]
    public async Task BridgeServerHandshakesAndCleansProcessSession()
    {
        var pipeName = "CipherVault.Bridge.Test." + Guid.NewGuid().ToString("N");
        var (server, options, handler) = CreateServer(pipeName);
        await using var serverInstance = server;
        var acceptTask = server.AcceptOnceAsync();

        await using var client = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                pipeName,
                options.ClientId,
                options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal) { "read" },
                options.AllowedClientPolicy,
                CurrentProcessServicePolicy(options.OwnerSid)));
        await client.DisposeAsync();

        var session = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(Guid.Empty, session.ProcessSessionId);
        Assert.NotEqual(Guid.Empty, session.BridgeInstanceId);
        Assert.Equal(options.OwnerSid, session.OwnerSid);
        Assert.Equal(options.ServiceSid, session.ServiceSid);
        Assert.Equal("mcp", session.ClientId);
        Assert.Equal(McpAccessMode.ReadOnly, session.AccessMode);
        Assert.Equal(
            "bridge-disconnected",
            Assert.Single(handler.ClosedReasons));
    }

    private static (
        BridgePipeServer Server,
        BridgePipeServerOptions Options,
        RecordingBridgeHandler Handler) CreateServer(string pipeName)
    {
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var servicePolicy = CurrentProcessServicePolicy(currentUser.Value);
        var clientPolicy = CurrentProcessBridgePolicy(currentUser.Value);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var security = PipeSecurityFactory.Create(
            [currentUser],
            includeAdministrators: false,
            includeSystem: false);
        var handler = new RecordingBridgeHandler();
        var options = new BridgePipeServerOptions
        {
            PipeName = pipeName,
            Security = security,
            ServiceInstanceId = Guid.NewGuid().ToString("D"),
            ServiceVersion = "1.0.0-test",
            ServiceIdentity = serviceIdentity,
            AllowedClientPolicy = clientPolicy,
            OwnerSid = currentUser.Value,
            ServiceSid = currentUser.Value,
            ClientId = "mcp",
            AccessMode = McpAccessMode.ReadOnly,
            BridgeSessionLifetime = TimeSpan.FromHours(12),
            Handler = handler
        };
        return (new BridgePipeServer(options), options, handler);
    }

    private static ProcessVerificationPolicy CurrentProcessServicePolicy(
        string userSid)
    {
        var imagePath = Environment.ProcessPath!;
        return new ProcessVerificationPolicy(
            VerifiedProcessKind.HostService,
            userSid,
            ExpectedLogonSid: null,
            (uint)Process.GetCurrentProcess().SessionId,
            [
                new ProcessImageIdentity(
                    VerifiedProcessKind.HostService,
                    imagePath,
                    WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath),
                    SignerThumbprint: null,
                    SignerSubject: null)
            ],
            AllowedSignerThumbprints: [],
            RequireSha256: true,
            RequireSignature: false);
    }

    private static ProcessVerificationPolicy CurrentProcessBridgePolicy(
        string userSid)
    {
        var imagePath = Environment.ProcessPath!;
        return new ProcessVerificationPolicy(
            VerifiedProcessKind.McpBridge,
            userSid,
            ExpectedLogonSid: null,
            (uint)Process.GetCurrentProcess().SessionId,
            [
                new ProcessImageIdentity(
                    VerifiedProcessKind.McpBridge,
                    imagePath,
                    WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath),
                    SignerThumbprint: null,
                    SignerSubject: null)
            ],
            AllowedSignerThumbprints: [],
            RequireSha256: true,
            RequireSignature: false);
    }

    private static VerifiedProcessIdentity VerifyIdentity(
        uint processId,
        ProcessVerificationPolicy policy)
    {
        var result = WindowsProcessIdentityVerifier.Verify(processId, policy);
        Assert.Equal(ProcessVerificationStatus.Verified, result.Status);
        using var evidence = Assert.IsType<VerifiedProcessEvidence>(
            result.Evidence);
        return evidence.Identity;
    }

    private sealed class RecordingBridgeHandler : IBridgeSessionHandler
    {
        public List<string> ClosedReasons { get; } = [];

        public Task<BridgeIssuedGrant> RequestGrantAsync(
            BridgeProcessSession session,
            BridgeGrantRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string> CallToolAsync(
            BridgeProcessSession session,
            AccessGrantContext? grant,
            BridgeToolCallRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task CloseSessionAsync(
            Guid processSessionId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            ClosedReasons.Add(reasonCode);
            return Task.CompletedTask;
        }
    }
}
