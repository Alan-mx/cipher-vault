using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Service;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;
using Microsoft.Extensions.Logging.Abstractions;

namespace CipherVault.Mcp.Tests;

public sealed class BrokerRecoveryEndToEndTests
{
    private static readonly IReadOnlySet<string> ReadScopes =
        new HashSet<string>(StringComparer.Ordinal) { "read" };

    [Fact]
    public async Task McpRecoversGrantAfterBrokerProcessKilled()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "Unable to resolve the current Windows user.");
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var hostImagePath = Environment.ProcessPath!;
        var brokerImagePath = FindTestBrokerExecutable();
        var hostPolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            hostImagePath);
        var brokerPolicy = Policy(
            VerifiedProcessKind.AuthBroker,
            currentUser.Value,
            currentSession,
            brokerImagePath);
        var bridgePolicy = Policy(
            VerifiedProcessKind.McpBridge,
            currentUser.Value,
            currentSession,
            hostImagePath);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            hostPolicy);
        var security = PipeSecurityFactory.Create(
            [currentUser],
            includeAdministrators: false,
            includeSystem: false);
        var brokerPipeName = "CipherVault.Mcp.BrokerRecoveryTest." +
            Guid.NewGuid().ToString("N");
        var bridgePipeName = "CipherVault.Mcp.BridgeRecoveryTest." +
            Guid.NewGuid().ToString("N");
        var registry = new BrokerConnectionRegistry();
        var activityProvider = new BrokerActivityProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var brokerAcceptLoop = new BrokerPipeAcceptLoop(
            new BrokerPipeServerOptions(
                brokerPipeName,
                security,
                Guid.NewGuid().ToString("D"),
                "1.0.0-test",
                serviceIdentity,
                [brokerPolicy],
                currentUser.Value,
                currentUser.Value,
                TimeSpan.FromMinutes(1),
                RequireInteractiveSession: false,
                ActivityProvider: activityProvider,
                HeartbeatTimeout: TimeSpan.FromSeconds(2)),
            registry,
            activityProvider,
            NullLogger<BrokerPipeAcceptLoop>.Instance);
        var brokerLoopTask = brokerAcceptLoop.RunAsync(timeout.Token);
        var clientOptionsPath = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-test-broker-{Guid.NewGuid():N}.json");
        var brokerClientOptions = new BrokerPipeClientOptions(
            brokerPipeName,
            brokerPolicy,
            hostPolicy,
            ActivityReportInterval: TimeSpan.FromMilliseconds(500));
        await File.WriteAllTextAsync(
            clientOptionsPath,
            JsonSerializer.Serialize(
                brokerClientOptions,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            timeout.Token);

        using var grants = new AccessGrantService();
        var brokerPrompt = new BrokerAccessGrantPrompt(registry);
        var bridgeOptions = new BridgePipeServerOptions
        {
            PipeName = bridgePipeName,
            Security = security,
            ServiceInstanceId = Guid.NewGuid().ToString("D"),
            ServiceVersion = "1.0.0-test",
            ServiceIdentity = serviceIdentity,
            AllowedClientPolicy = bridgePolicy,
            OwnerSid = currentUser.Value,
            ServiceSid = currentUser.Value,
            ClientId = "mcp",
            AccessMode = McpAccessMode.ReadOnly,
            BridgeSessionLifetime = TimeSpan.FromMinutes(1),
            Handler = new BrokerGrantHandler(grants, brokerPrompt)
        };
        await using var bridgeServer = new BridgePipeServer(bridgeOptions);
        var bridgeAcceptTask = bridgeServer.AcceptOnceAsync(timeout.Token);
        Process? firstBroker = null;
        Process? secondBroker = null;
        BridgePipeClientConnection? bridgeConnection = null;
        StrictMcpBridgeClient? mcp = null;
        var brokerStarts = 0;

        try
        {
            firstBroker = StartTestBroker(clientOptionsPath);
            await WaitUntilAsync(
                () => RegisteredProcessId(registry) == (uint?)firstBroker.Id,
                TimeSpan.FromSeconds(10));

            var settings = new StrictBridgeSettings(
                new CipherVaultInstallPublicManifest(),
                string.Empty,
                bridgePipeName,
                "mcp",
                McpAccessMode.ReadOnly,
                ReadScopes);
            bridgeConnection = await BridgePipeClientConnection.ConnectAsync(
                new BridgePipeClientOptions(
                    bridgePipeName,
                    "mcp",
                    McpAccessMode.ReadOnly,
                    ReadScopes,
                    bridgePolicy,
                    hostPolicy),
                timeout.Token);
            mcp = new StrictMcpBridgeClient(
                settings,
                bridgeConnection,
                _ => Task.FromResult<IBridgePipeClientConnection>(
                    bridgeConnection),
                _ =>
                {
                    Interlocked.Increment(ref brokerStarts);
                    secondBroker = StartTestBroker(clientOptionsPath);
                    return Task.FromResult(true);
                });

            firstBroker.Kill(entireProcessTree: true);
            await firstBroker.WaitForExitAsync(timeout.Token);
            await WaitUntilAsync(
                () => !registry.IsAvailable,
                TimeSpan.FromSeconds(10));

            var recovery = Stopwatch.StartNew();
            await mcp.RequestGrantAsync(timeout.Token);
            recovery.Stop();

            Assert.Contains("read", mcp.GrantedScopes);
            Assert.NotNull(secondBroker);
            Assert.Equal(1, brokerStarts);
            Assert.True(
                recovery.Elapsed < TimeSpan.FromSeconds(12),
                $"MCP recovery took {recovery.Elapsed}.");
            await WaitUntilAsync(
                () => RegisteredProcessId(registry) == (uint?)secondBroker.Id,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (mcp is not null)
            {
                await mcp.RevokeGrantAsync();
            }
            else if (bridgeConnection is not null)
            {
                await bridgeConnection.DisposeAsync();
            }
            foreach (var process in new[] { firstBroker, secondBroker })
            {
                if (process is null)
                {
                    continue;
                }
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    await process.WaitForExitAsync();
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception)
                {
                }
                process.Dispose();
            }
            timeout.Cancel();
            try
            {
                await bridgeAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
                await brokerLoopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or TimeoutException)
            {
            }
            File.Delete(clientOptionsPath);
        }
    }

    private static ProcessVerificationPolicy Policy(
        VerifiedProcessKind kind,
        string userSid,
        uint sessionId,
        string imagePath)
    {
        return new ProcessVerificationPolicy(
            kind,
            userSid,
            ExpectedLogonSid: null,
            sessionId,
            [
                new ProcessImageIdentity(
                    kind,
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

    private static uint? RegisteredProcessId(BrokerConnectionRegistry registry)
    {
        try
        {
            return registry.GetRequired().ClientIdentity.ProcessId;
        }
        catch (AdminPipeRpcException exception) when (
            exception.Code == "approval_broker_unavailable")
        {
            return null;
        }
    }

    private static string FindTestBrokerExecutable()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name
            ?? throw new InvalidOperationException(
                "Unable to resolve test build configuration.");
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var executable = Path.Combine(
            repositoryRoot,
            "tests",
            "CipherVault.TestBroker",
            "bin",
            configuration,
            "net10.0-windows",
            "CipherVault.TestBroker.exe");
        Assert.True(
            File.Exists(executable),
            $"Test Broker executable was not found: {executable}");
        return executable;
    }

    private static Process StartTestBroker(string optionsPath)
    {
        var startInfo = new ProcessStartInfo(FindTestBrokerExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(optionsPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Unable to start the test Broker process.");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "MCP Broker recovery test condition was not reached.");
            }
            await Task.Delay(25);
        }
    }

    private sealed class BrokerGrantHandler(
        AccessGrantService grants,
        BrokerAccessGrantPrompt prompt) : IBridgeSessionHandler
    {
        private readonly Guid _vaultSessionId = Guid.NewGuid();
        private readonly DateTimeOffset _vaultExpiresAt =
            DateTimeOffset.UtcNow.AddMinutes(30);

        public async Task<BridgeIssuedGrant> RequestGrantAsync(
            BridgeProcessSession session,
            BridgeGrantRequest request,
            CancellationToken cancellationToken)
        {
            var requestedScopes = ParseScopes(request.Scopes);
            var outcome = await grants.RequestAsync(
                session.ClientId,
                session.BridgeInstanceId,
                session.AccessMode,
                requestedScopes,
                new AccessGrantVaultGeneration(
                    _vaultSessionId,
                    _vaultExpiresAt),
                async (approval, token) =>
                {
                    var result = await prompt.RequestAsync(
                        new HostAccessGrantPromptRequest(
                            approval.ClientId,
                            approval.ClientInstanceId,
                            approval.TokenAccessMode,
                            approval.RequestedScopes,
                            approval.AllowedScopes,
                            grants.IdleLifetime,
                            grants.HardLifetime),
                        token);
                    return new AccessGrantApprovalResult(
                        result.Approved,
                        result.ApprovedScopes);
                },
                session.ProcessSessionId,
                cancellationToken);
            var issued = outcome.IssuedGrant
                ?? throw new AdminPipeRpcException(
                    outcome.ReasonCode ?? "access_denied",
                    "Bridge Access Grant was denied.");
            var record = issued.Record;
            return new BridgeIssuedGrant(
                new BridgeGrantData(
                    record.GrantId,
                    record.ClientId,
                    session.AccessMode == McpAccessMode.ReadWrite
                        ? "readWrite"
                        : "readOnly",
                    AccessGrantScopes.ToNames(record.Scopes)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray(),
                    record.HardExpiresAt!.Value),
                new AccessGrantContext(
                    record.GrantId,
                    record.ClientId,
                    record.ClientInstanceId,
                    record.Scopes,
                    record.VaultSessionId!.Value,
                    record.VerifiedProcessSessionId));
        }

        public Task<string> CallToolAsync(
            BridgeProcessSession session,
            AccessGrantContext? grant,
            BridgeToolCallRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<BridgeStatusData> GetStatusAsync(
            BridgeProcessSession session,
            AccessGrantContext? grant,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string> scopes = grant is null
                ? Array.Empty<string>()
                : AccessGrantScopes.ToNames(grant.Scopes).ToArray();
            return Task.FromResult(new BridgeStatusData(
                grant is not null,
                session.ClientId,
                session.AccessMode == McpAccessMode.ReadWrite
                    ? "readWrite"
                    : "readOnly",
                session.ProcessSessionId,
                session.BridgeInstanceId,
                session.ExpiresAt,
                scopes,
                GrantExpiresAt: null));
        }

        public Task CloseSessionAsync(
            Guid processSessionId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            return grants.RevokeByProcessSessionAsync(
                processSessionId,
                reasonCode);
        }

        private static AccessGrantScope ParseScopes(
            IReadOnlyList<string> values)
        {
            var scopes = AccessGrantScope.None;
            foreach (var value in values)
            {
                scopes |= value.ToLowerInvariant() switch
                {
                    "read" => AccessGrantScope.Read,
                    "write" => AccessGrantScope.Write,
                    "delete" => AccessGrantScope.Delete,
                    _ => throw new InvalidDataException(
                        "Unsupported test scope.")
                };
            }
            return AccessGrantScopes.Normalize(scopes);
        }
    }
}
