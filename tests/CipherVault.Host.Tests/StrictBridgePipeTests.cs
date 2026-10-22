using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using CipherVault.Core;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Service;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task StrictBridgeExposesNineCompatibleTools()
    {
        await using var environment = await CreateRealBridgeEnvironmentAsync();
        var acceptTask = environment.Server.AcceptOnceAsync();
        await using var client = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                environment.Options.PipeName,
                environment.Options.ClientId,
                environment.Options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal) { "read", "write", "delete" },
                environment.Options.AllowedClientPolicy,
                CurrentProcessServicePolicy(environment.Options.OwnerSid)));

        var grant = await client.RequestGrantAsync();
        Assert.Equal("readWrite", grant.AccessMode);

        using var status = await CallToolAsync(client, "vault_status", []);
        Assert.True(status.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "mcp",
            status.RootElement
                .GetProperty("vault")
                .GetProperty("clientId")
                .GetString());
        Assert.Equal(
            "unlocked",
            status.RootElement
                .GetProperty("session")
                .GetProperty("state")
                .GetString());

        using var emptyCatalog = await CallToolAsync(
            client,
            "list_catalog",
            [("limit", 10)]);
        Assert.True(emptyCatalog.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            JsonValueKind.Object,
            emptyCatalog.RootElement.GetProperty("page").ValueKind);

        using var created = await CallToolAsync(
            client,
            "create_note",
            [
                ("title", "Strict Bridge Note"),
                ("content", "Searchable strict bridge content."),
                ("folder", "notes"),
                ("tags", new[] { "strict", "bridge" })
            ]);
        Assert.True(created.RootElement.GetProperty("ok").GetBoolean());
        var documentId = Guid.Parse(
            created.RootElement
                .GetProperty("document")
                .GetProperty("documentId")
                .GetString()!);
        Assert.Equal(1, created.RootElement.GetProperty("document").GetProperty("revision").GetInt32());

        using var read = await CallToolAsync(
            client,
            "read_note",
            [("documentId", documentId), ("maxChars", 100)]);
        Assert.Equal(
            "Strict Bridge Note",
            read.RootElement.GetProperty("document").GetProperty("title").GetString());

        using var appended = await CallToolAsync(
            client,
            "append_note",
            [
                ("documentId", documentId),
                ("expectedRevision", 1),
                ("content", "Appended strict content.")
            ]);
        Assert.Equal(2, appended.RootElement.GetProperty("document").GetProperty("revision").GetInt32());

        using var updated = await CallToolAsync(
            client,
            "update_note",
            [
                ("documentId", documentId),
                ("expectedRevision", 2),
                ("title", "Updated Strict Bridge Note"),
                ("content", "Updated searchable strict bridge content.")
            ]);
        Assert.Equal(3, updated.RootElement.GetProperty("document").GetProperty("revision").GetInt32());

        using var search = await CallToolAsync(
            client,
            "search_notes",
            [("query", "strict bridge")]);
        Assert.True(search.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(
            search.RootElement.GetProperty("page").GetProperty("hits")
                .EnumerateArray().Count() >= 1);

        using var scan = await CallToolAsync(client, "scan_notes", [("limit", 10)]);
        Assert.True(scan.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(scan.RootElement.GetProperty("page").GetProperty("items").EnumerateArray().Count() >= 1);

        using var deleted = await CallToolAsync(
            client,
            "delete_note",
            [("documentId", documentId), ("expectedRevision", 3)]);
        Assert.True(
            deleted.RootElement.GetProperty("ok").GetBoolean(),
            deleted.RootElement.GetRawText());
        Assert.Equal(
            documentId,
            deleted.RootElement.GetProperty("document").GetProperty("documentId").GetGuid());

        await client.DisposeAsync();
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(environment.Grants.List());
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

    private static async Task<JsonDocument> CallToolAsync(
        BridgePipeClientConnection client,
        string toolName,
        IReadOnlyCollection<(string Name, object? Value)> arguments)
    {
        var payload = arguments.ToDictionary(
            item => item.Name,
            item => item.Value,
            StringComparer.Ordinal);
        var result = await client.CallToolAsync(
            toolName,
            payload,
            CancellationToken.None);
        return JsonDocument.Parse(result);
    }

    private static async Task<StrictBridgeTestEnvironment> CreateRealBridgeEnvironmentAsync()
    {
        var fixture = await HostTestFixture.CreateAsync();
        var sessions = fixture.App.Services.GetRequiredService<HostSessionCoordinator>();
        var grants = fixture.App.Services.GetRequiredService<AccessGrantService>();
        var gateway = fixture.App.Services.GetRequiredService<HostVaultGateway>();
        await sessions.UnlockAsync(HostCredentialPreference.Password);

        var paths = CipherVaultServicePaths.Create(
            Path.Combine(fixture.Root, "ProgramFiles"),
            Path.Combine(fixture.Root, "ProgramData"),
            Path.Combine(fixture.Root, "LocalAppData"));
        var provider = new StrictAdminVaultRpcProvider(
            gateway,
            sessions,
            new ApprovalStore(),
            new RejectingApprovalPrompt(),
            new TransferCoordinator(paths),
            new RejectingTransferPrompt());
        var handler = new StrictBridgeHandler(
            sessions,
            grants,
            fixture.AccessGrantPrompt,
            provider);
        var pipeName = "CipherVault.Bridge.Test." + Guid.NewGuid().ToString("N");
        var (server, options) = CreateServer(pipeName, handler, McpAccessMode.ReadWrite);
        return new StrictBridgeTestEnvironment(
            fixture,
            server,
            options,
            grants,
            provider);
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

    private sealed class RejectingApprovalPrompt : IApprovalPrompt
    {
        public Task<HostApprovalDecision> RequestAsync(
            HostApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RejectingTransferPrompt : ITransferPrompt
    {
        public Task<HostTransferResult> TransferAsync(
            HostTransferRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StrictBridgeTestEnvironment : IAsyncDisposable
    {
        public StrictBridgeTestEnvironment(
            HostTestFixture fixture,
            BridgePipeServer server,
            BridgePipeServerOptions options,
            AccessGrantService grants,
            StrictAdminVaultRpcProvider provider)
        {
            Fixture = fixture;
            Server = server;
            Options = options;
            Grants = grants;
            Provider = provider;
        }

        public HostTestFixture Fixture { get; }
        public BridgePipeServer Server { get; }
        public BridgePipeServerOptions Options { get; }
        public AccessGrantService Grants { get; }
        public StrictAdminVaultRpcProvider Provider { get; }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Provider.Dispose();
            await Fixture.DisposeAsync();
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
