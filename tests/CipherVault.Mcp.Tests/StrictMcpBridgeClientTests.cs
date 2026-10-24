using System.Text.Json;
using System.Diagnostics;
using System.Security.Principal;
using CipherVault.Core.Configuration;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;

namespace CipherVault.Mcp.Tests;

public sealed class StrictMcpBridgeClientTests
{
    private static readonly IReadOnlySet<string> ReadScopes =
        new HashSet<string>(StringComparer.Ordinal) { "read" };
    private static readonly IReadOnlySet<string> ReadWriteScopes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "read",
            "write"
        };

    [Fact]
    public async Task ReadToolReconnectsAndRetriesAfterTransportFailure()
    {
        var initial = new FakeBridgeConnection
        {
            CallHandler = (_, _, _) =>
                throw new IOException("Pipe is broken.")
        };
        var replacement = new FakeBridgeConnection
        {
            CallHandler = (_, _, _) => Task.FromResult("{\"ok\":true}")
        };
        var client = CreateClient(initial, replacement);

        var result = await client.CallToolAsync(
            "read_note",
            Arguments(),
            CancellationToken.None);

        Assert.Equal("{\"ok\":true}", result);
        Assert.Equal(1, initial.DisposeCount);
        Assert.Equal(1, replacement.GrantRequests);
        Assert.Equal(1, replacement.ToolCalls);
    }

    [Fact]
    public async Task WriteToolReconnectsButDoesNotReplayAfterTransportFailure()
    {
        var initial = new FakeBridgeConnection
        {
            CallHandler = (_, _, _) =>
                throw new IOException("Pipe is broken.")
        };
        var replacement = new FakeBridgeConnection
        {
            CallHandler = (_, _, _) => Task.FromResult("{\"ok\":true}")
        };
        var client = CreateClient(initial, replacement);

        var result = await client.CallToolAsync(
            "create_note",
            Arguments(),
            CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "bridge_connection_reset",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Equal(1, replacement.GrantRequests);
        Assert.Equal(0, replacement.ToolCalls);
    }

    [Fact]
    public async Task BrokerUnavailableTriggersOneBrokerRecovery()
    {
        var grantRequests = 0;
        var connection = new FakeBridgeConnection
        {
            GrantHandler = (_, _, _) =>
            {
                grantRequests++;
                if (grantRequests == 1)
                {
                    throw new PipeRpcException(
                        "approval_broker_unavailable",
                        "Broker unavailable.");
                }
                return Task.FromResult(Grant());
            }
        };
        var brokerStarts = 0;
        var client = new StrictMcpBridgeClient(
            Settings(),
            connection,
            _ => Task.FromResult<IBridgePipeClientConnection>(connection),
            _ =>
            {
                brokerStarts++;
                return Task.FromResult(true);
            });

        await client.RequestGrantAsync(CancellationToken.None);

        Assert.Equal(2, grantRequests);
        Assert.Equal(1, brokerStarts);
        Assert.Contains("write", client.GrantedScopes);
    }

    [Fact]
    public async Task GrantRequestReconnectsAfterTransportFailure()
    {
        var initial = new FakeBridgeConnection
        {
            GrantHandler = (_, _, _) =>
                throw new IOException("Pipe is broken.")
        };
        var replacement = new FakeBridgeConnection();
        var client = CreateClient(initial, replacement);

        await client.RequestGrantAsync(CancellationToken.None);

        Assert.Equal(1, initial.DisposeCount);
        Assert.Equal(1, replacement.GrantRequests);
        Assert.Contains("read", client.GrantedScopes);
    }

    [Fact]
    public async Task RealBridgeDisconnectReconnectsAndReauthorizes()
    {
        var pipeName = "CipherVault.Mcp.Reconnect." +
            Guid.NewGuid().ToString("N");
        var (hostPolicy, bridgePolicy) = Policies();
        var serviceIdentity = VerifyIdentity(hostPolicy);
        var serverOptions = new BridgePipeServerOptions
        {
            PipeName = pipeName,
            Security = PipeSecurityFactory.Create(
                [WindowsIdentity.GetCurrent().User!],
                includeAdministrators: false,
                includeSystem: false),
            ServiceInstanceId = Guid.NewGuid().ToString("D"),
            ServiceVersion = "1.0.0-test",
            ServiceIdentity = serviceIdentity,
            AllowedClientPolicy = bridgePolicy,
            OwnerSid = WindowsIdentity.GetCurrent().User!.Value,
            ServiceSid = WindowsIdentity.GetCurrent().User!.Value,
            ClientId = "mcp",
            AccessMode = McpAccessMode.ReadOnly,
            BridgeSessionLifetime = TimeSpan.FromHours(1),
            Handler = new ReconnectBridgeHandler()
        };
        var clientOptions = new BridgePipeClientOptions(
            pipeName,
            "mcp",
            McpAccessMode.ReadOnly,
            ReadScopes,
            bridgePolicy,
            hostPolicy);
        await using var firstServer = new BridgePipeServer(serverOptions);
        var firstAccept = firstServer.AcceptOnceAsync();
        var initial = await BridgePipeClientConnection.ConnectAsync(
            clientOptions);
        var client = new StrictMcpBridgeClient(
            Settings(),
            initial,
            token => ConnectAsync(clientOptions, token),
            _ => Task.FromResult(true));

        var first = await client.CallToolAsync(
            "read_note",
            Arguments(),
            CancellationToken.None);
        Assert.Equal("{\"ok\":true}", first);

        await firstServer.DisposeAsync();
        try
        {
            await firstAccept.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }

        await using var replacementServer = new BridgePipeServer(serverOptions);
        var replacementAccept = replacementServer.AcceptOnceAsync();
        var second = await client.CallToolAsync(
            "read_note",
            Arguments(),
            CancellationToken.None);

        Assert.Equal("{\"ok\":true}", second);
        await client.RevokeGrantAsync();
        try
        {
            await replacementAccept.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
    }

    private static StrictMcpBridgeClient CreateClient(
        FakeBridgeConnection initial,
        FakeBridgeConnection replacement)
    {
        return new StrictMcpBridgeClient(
            Settings(),
            initial,
            _ => Task.FromResult<IBridgePipeClientConnection>(replacement),
            _ => Task.FromResult(true));
    }

    private static StrictBridgeSettings Settings()
    {
        return new StrictBridgeSettings(
            new CipherVaultInstallPublicManifest(),
            "manifest.json",
            "bridge-pipe",
            "mcp",
            McpAccessMode.ReadWrite,
            ReadWriteScopes);
    }

    private static IReadOnlyDictionary<string, object?> Arguments()
    {
        return new Dictionary<string, object?>();
    }

    private static BridgeGrantData Grant()
    {
        return new BridgeGrantData(
            Guid.NewGuid(),
            "mcp",
            "readWrite",
            ["read", "write"],
            DateTimeOffset.UtcNow.AddHours(1));
    }

    private static (
        ProcessVerificationPolicy Host,
        ProcessVerificationPolicy Bridge) Policies()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var sessionId = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(
            imagePath);
        var host = new ProcessVerificationPolicy(
            VerifiedProcessKind.HostService,
            user.Value,
            ExpectedLogonSid: null,
            sessionId,
            [
                new ProcessImageIdentity(
                    VerifiedProcessKind.HostService,
                    imagePath,
                    imageHash,
                    SignerThumbprint: null,
                    SignerSubject: null)
            ],
            AllowedSignerThumbprints: [],
            RequireSha256: true,
            RequireSignature: false);
        var bridge = new ProcessVerificationPolicy(
            VerifiedProcessKind.McpBridge,
            user.Value,
            ExpectedLogonSid: null,
            sessionId,
            [
                new ProcessImageIdentity(
                    VerifiedProcessKind.McpBridge,
                    imagePath,
                    imageHash,
                    SignerThumbprint: null,
                    SignerSubject: null)
            ],
            AllowedSignerThumbprints: [],
            RequireSha256: true,
            RequireSignature: false);
        return (host, bridge);
    }

    private static VerifiedProcessIdentity VerifyIdentity(
        ProcessVerificationPolicy policy)
    {
        var result = WindowsProcessIdentityVerifier.Verify(
            (uint)Environment.ProcessId,
            policy);
        Assert.Equal(ProcessVerificationStatus.Verified, result.Status);
        using var evidence = Assert.IsType<VerifiedProcessEvidence>(
            result.Evidence);
        return evidence.Identity;
    }

    private static async Task<IBridgePipeClientConnection> ConnectAsync(
        BridgePipeClientOptions options,
        CancellationToken cancellationToken)
    {
        return await BridgePipeClientConnection.ConnectAsync(
            options,
            cancellationToken);
    }

    private sealed class FakeBridgeConnection : IBridgePipeClientConnection
    {
        public bool IsUsable { get; set; } = true;
        public DateTimeOffset ExpiresAt { get; set; } =
            DateTimeOffset.UtcNow.AddHours(1);
        public int GrantRequests { get; private set; }
        public int ToolCalls { get; private set; }
        public int DisposeCount { get; private set; }
        public Func<
            string,
            IReadOnlyDictionary<string, object?>,
            CancellationToken,
            Task<BridgeGrantData>>? GrantHandler
        { get; init; }
        public Func<
            string,
            IReadOnlyDictionary<string, object?>,
            CancellationToken,
            Task<string>>? CallHandler
        { get; init; }

        public Task<BridgeGrantData> RequestGrantAsync(
            CancellationToken cancellationToken = default)
        {
            GrantRequests++;
            return GrantHandler is null
                ? Task.FromResult(Grant())
                : GrantHandler(
                    "bridge.grant.request",
                    Arguments(),
                    cancellationToken);
        }

        public Task<string> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            ToolCalls++;
            return CallHandler is null
                ? Task.FromResult("{\"ok\":true}")
                : CallHandler(toolName, arguments, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsUsable = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReconnectBridgeHandler : IBridgeSessionHandler
    {
        public Task<BridgeIssuedGrant> RequestGrantAsync(
            BridgeProcessSession session,
            BridgeGrantRequest request,
            CancellationToken cancellationToken)
        {
            var grantId = Guid.NewGuid();
            return Task.FromResult(new BridgeIssuedGrant(
                new BridgeGrantData(
                    grantId,
                    session.ClientId,
                    "readOnly",
                    ["read"],
                    DateTimeOffset.UtcNow.AddHours(1)),
                new AccessGrantContext(
                    grantId,
                    session.ClientId,
                    session.BridgeInstanceId,
                    AccessGrantScope.Read,
                    Guid.NewGuid(),
                    session.ProcessSessionId)));
        }

        public Task<string> CallToolAsync(
            BridgeProcessSession session,
            AccessGrantContext? grant,
            BridgeToolCallRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult("{\"ok\":true}");
        }

        public Task<BridgeStatusData> GetStatusAsync(
            BridgeProcessSession session,
            AccessGrantContext? grant,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new BridgeStatusData(
                true,
                session.ClientId,
                "readOnly",
                session.ProcessSessionId,
                session.BridgeInstanceId,
                session.ExpiresAt,
                ["read"],
                DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task CloseSessionAsync(
            Guid processSessionId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
