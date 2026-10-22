using System.Diagnostics;
using System.IO;
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
        var handler = new RecordingBridgeHandler();
        var (server, options) = CreateServer(pipeName, handler);
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

    [Fact]
    public async Task ActiveGrantIsRevokedWhenBridgeDisconnects()
    {
        using var grants = new AccessGrantService();
        var prompt = new ImmediateAccessGrantPrompt();
        var handler = new RealGrantBridgeHandler(grants, prompt);
        var pipeName = "CipherVault.Bridge.Test." + Guid.NewGuid().ToString("N");
        var (server, options) = CreateServer(pipeName, handler);
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

        var grant = await client.RequestGrantAsync();
        Assert.Equal("mcp", grant.ClientId);
        Assert.Contains("read", grant.Scopes);
        Assert.Single(grants.List());

        await client.DisposeAsync();
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(grants.List());
    }

    [Fact]
    public async Task PendingGrantIsCancelledWhenBridgeDisconnects()
    {
        using var grants = new AccessGrantService();
        var promptStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var promptCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new ImmediateAccessGrantPrompt
        {
            Handler = async (_, cancellationToken) =>
            {
                promptStarted.TrySetResult();
                using var registration = cancellationToken.Register(
                    () => promptCancelled.TrySetResult());
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                return new HostAccessGrantPromptResult(
                    true,
                    AccessGrantScope.Read);
            }
        };
        var handler = new RealGrantBridgeHandler(grants, prompt);
        var pipeName = "CipherVault.Bridge.Test." + Guid.NewGuid().ToString("N");
        var (server, options) = CreateServer(pipeName, handler);
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
        var grantTask = client.RequestGrantAsync();
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await client.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            grantTask.WaitAsync(TimeSpan.FromSeconds(5)));
        var acceptException = await Assert.ThrowsAnyAsync<Exception>(() =>
            acceptTask.WaitAsync(TimeSpan.FromSeconds(5)));
        _ = acceptException;

        await promptCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(prompt.Canceled);
        Assert.Empty(grants.List());
    }

    private static (
        BridgePipeServer Server,
        BridgePipeServerOptions Options) CreateServer(
        string pipeName,
        IBridgeSessionHandler handler,
        McpAccessMode accessMode = McpAccessMode.ReadOnly)
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
            AccessMode = accessMode,
            BridgeSessionLifetime = TimeSpan.FromHours(12),
            Handler = handler
        };
        return (new BridgePipeServer(options), options);
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

    private sealed class RealGrantBridgeHandler(
        AccessGrantService grants,
        ImmediateAccessGrantPrompt prompt) : IBridgeSessionHandler
    {
        private readonly Guid _vaultSessionId = Guid.NewGuid();
        private readonly DateTimeOffset _vaultExpiresAt =
            DateTimeOffset.UtcNow.AddHours(3);

        public async Task<BridgeIssuedGrant> RequestGrantAsync(
            BridgeProcessSession session,
            BridgeGrantRequest request,
            CancellationToken cancellationToken)
        {
            var outcome = await grants.RequestAsync(
                session.ClientId,
                session.BridgeInstanceId,
                session.AccessMode,
                ParseScopes(request.Scopes),
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
                AgentClientIdentityKind.VerifiedBridgeProcess,
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
                    record.IdentityKind,
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

    private sealed class ImmediateAccessGrantPrompt : IHostAccessGrantPrompt
    {
        public TaskCompletionSource CancelledSignal { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<
            HostAccessGrantPromptRequest,
            CancellationToken,
            Task<HostAccessGrantPromptResult>>? Handler
        { get; init; }
        public bool Canceled { get; private set; }

        public async Task<HostAccessGrantPromptResult> RequestAsync(
            HostAccessGrantPromptRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Handler is null)
            {
                return new HostAccessGrantPromptResult(
                    true,
                    request.RequestedScopes);
            }
            try
            {
                return await Handler(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                CancelledSignal.TrySetResult();
                throw;
            }
        }
    }
}
