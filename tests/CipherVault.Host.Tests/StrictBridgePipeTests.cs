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
using CipherVault.Host.Mcp;
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
    public async Task BridgeServerRejectsClientWhenOwnerSidDiffers()
    {
        var handler = new RecordingBridgeHandler();
        var pipeName = "CipherVault.Bridge.Test." + Guid.NewGuid().ToString("N");
        var baseOptions = CreateBridgeOptions(pipeName, handler);
        var options = baseOptions with
        {
            OwnerSid = "S-1-5-21-9999999999-9999999999-9999999999-9999"
        };
        await using var server = new BridgePipeServer(options);
        var acceptTask = server.AcceptOnceAsync();
        var connectTask = BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                pipeName,
                options.ClientId,
                options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal) { "read" },
                options.AllowedClientPolicy,
                CurrentProcessServicePolicy(
                    WindowsIdentity.GetCurrent().User!.Value)));

        var rejected = await Assert.ThrowsAsync<ProcessIdentityRejectedException>(
            () => acceptTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("bridge_owner_mismatch", rejected.ReasonCode);
        await Assert.ThrowsAnyAsync<Exception>(
            () => connectTask.WaitAsync(TimeSpan.FromSeconds(5)));
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
    public async Task BridgeSupportsPingStatusAndClientRequestedClose()
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

        var ping = await client.PingAsync();
        Assert.NotEqual(default, ping.ServerTime);

        var beforeGrant = await client.GetStatusAsync();
        Assert.False(beforeGrant.GrantActive);
        Assert.Empty(beforeGrant.GrantScopes);

        _ = await client.RequestGrantAsync();
        var active = await client.GetStatusAsync();
        Assert.True(active.GrantActive);
        Assert.Contains("read", active.GrantScopes);

        var closed = await client.CloseSessionAsync("test-close");
        Assert.Equal("test-close", closed.Reason);
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(grants.List());
    }

    [Fact]
    public async Task BridgeSessionExpiryClosesConnectionAndRevokesGrant()
    {
        using var grants = new AccessGrantService();
        var handler = new RealGrantBridgeHandler(
            grants,
            new ImmediateAccessGrantPrompt());
        var pipeName = "CipherVault.Bridge.Test." + Guid.NewGuid().ToString("N");
        var options = CreateBridgeOptions(
            pipeName,
            handler,
            McpAccessMode.ReadOnly) with
        {
            BridgeSessionLifetime = TimeSpan.FromSeconds(1)
        };
        await using var server = new BridgePipeServer(options);
        var acceptTask = server.AcceptOnceAsync();
        await using var client = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                pipeName,
                options.ClientId,
                options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal) { "read" },
                options.AllowedClientPolicy,
                CurrentProcessServicePolicy(options.OwnerSid)));

        _ = await client.RequestGrantAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<PipeRpcException>(
            () => client.GetStatusAsync());
        Assert.Equal("bridge_session_expired", exception.Code);
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(grants.List());
    }

    [Fact]
    public async Task CloseWithoutReasonUsesClientRequested()
    {
        using var grants = new AccessGrantService();
        var handler = new RealGrantBridgeHandler(
            grants,
            new ImmediateAccessGrantPrompt());
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

        var closed = await client.CloseSessionAsync();

        Assert.Equal("client-requested", closed.Reason);
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(grants.List());
    }

    [Fact]
    public async Task OverlongCloseReasonIsRejectedWithoutClosingSession()
    {
        using var grants = new AccessGrantService();
        var handler = new RealGrantBridgeHandler(
            grants,
            new ImmediateAccessGrantPrompt());
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
        _ = await client.RequestGrantAsync();

        var exception = await Assert.ThrowsAsync<PipeRpcException>(() =>
            client.CloseSessionAsync(new string('x', 201)));

        Assert.Equal("invalid_input", exception.Code);
        var status = await client.GetStatusAsync();
        Assert.True(status.GrantActive);
        Assert.Contains("read", status.GrantScopes);

        var closed = await client.CloseSessionAsync("test-cleanup");
        Assert.Equal("test-cleanup", closed.Reason);
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(grants.List());
    }

    [Fact]
    public async Task StatusReportsRevokedGrantThroughStrictHandler()
    {
        await using var environment = await CreateRealBridgeEnvironmentAsync();
        var acceptTask = environment.Server.AcceptOnceAsync();
        await using var client = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                environment.Options.PipeName,
                environment.Options.ClientId,
                environment.Options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal) { "read" },
                environment.Options.AllowedClientPolicy,
                CurrentProcessServicePolicy(environment.Options.OwnerSid)));
        var grant = await client.RequestGrantAsync();
        Assert.True((await client.GetStatusAsync()).GrantActive);

        Assert.Equal(
            AccessGrantRevokeStatus.Revoked,
            await environment.Grants.RevokeByIdAsync(
                grant.GrantId,
                "admin-requested"));

        var revoked = await client.GetStatusAsync();
        Assert.False(revoked.GrantActive);
        Assert.Empty(revoked.GrantScopes);
        Assert.Null(revoked.GrantExpiresAt);

        _ = await client.CloseSessionAsync();
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task VaultStatusWorksWithoutGrantWhileLocked()
    {
        await using var environment = await CreateRealBridgeEnvironmentAsync();
        var sessions = environment.Fixture.App.Services
            .GetRequiredService<HostSessionCoordinator>();
        await sessions.LockAsync(
            "test-lock",
            CancellationToken.None);
        var acceptTask = environment.Server.AcceptOnceAsync();
        await using var client = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                environment.Options.PipeName,
                environment.Options.ClientId,
                environment.Options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal) { "read" },
                environment.Options.AllowedClientPolicy,
                CurrentProcessServicePolicy(environment.Options.OwnerSid)));

        using var status = await CallToolAsync(client, "vault_status", []);

        Assert.True(status.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "locked",
            status.RootElement
                .GetProperty("session")
                .GetProperty("state")
                .GetString());
        _ = await client.CloseSessionAsync();
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StatusReportsExpiredGrantThroughStrictHandler()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var grants = new AccessGrantService(
            timeProvider: time,
            idleLifetime: TimeSpan.FromSeconds(10));
        await using var environment = await CreateRealBridgeEnvironmentAsync(grants);
        var acceptTask = environment.Server.AcceptOnceAsync();
        await using var client = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                environment.Options.PipeName,
                environment.Options.ClientId,
                environment.Options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal) { "read" },
                environment.Options.AllowedClientPolicy,
                CurrentProcessServicePolicy(environment.Options.OwnerSid)));
        _ = await client.RequestGrantAsync();
        Assert.True((await client.GetStatusAsync()).GrantActive);

        time.Advance(TimeSpan.FromSeconds(10));

        var expired = await client.GetStatusAsync();
        Assert.False(expired.GrantActive);
        Assert.Empty(expired.GrantScopes);
        Assert.Null(expired.GrantExpiresAt);

        _ = await client.CloseSessionAsync();
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailedToolCallClearsGrantAndAllowsReauthorization()
    {
        var handler = new RevokedGrantBridgeHandler();
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

        _ = await client.RequestGrantAsync();
        using var failed = await CallToolAsync(client, "vault_status", []);
        Assert.False(failed.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "access_grant_revoked",
            failed.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());

        var renewed = await client.RequestGrantAsync();

        Assert.Equal("mcp", renewed.ClientId);
        Assert.Contains("read", renewed.Scopes);
        await client.DisposeAsync();
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
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

    [Fact]
    public async Task ScopedGrantRestrictsBridgeReadsAndWritesToFolder()
    {
        await using var environment = await CreateRealBridgeEnvironmentAsync();
        var gateway = environment.Fixture.App.Services
            .GetRequiredService<HostVaultGateway>();
        var other = await gateway.ExecuteAsync(
            (vault, token) => vault.CreateNoteAsync(
                "Other Note",
                "Other",
                "md",
                "# Other Note",
                null,
                VaultActorKind.System,
                token));
        var acceptTask = environment.Server.AcceptOnceAsync();
        await using var client = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                environment.Options.PipeName,
                environment.Options.ClientId,
                environment.Options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "read:Projects",
                    "write:Projects"
                },
                environment.Options.AllowedClientPolicy,
                CurrentProcessServicePolicy(environment.Options.OwnerSid)));
        var grant = await client.RequestGrantAsync();
        Assert.Contains("read:projects", grant.Scopes);
        Assert.Contains("write:projects", grant.Scopes);

        using var created = await CallToolAsync(
            client,
            "create_note",
            [
                ("title", "Project Note"),
                ("folder", "Projects"),
                ("content", "# Project Note")
            ]);
        Assert.True(
            created.RootElement.GetProperty("ok").GetBoolean(),
            created.RootElement.GetRawText());

        using var deniedCreate = await CallToolAsync(
            client,
            "create_note",
            [
                ("title", "Denied Note"),
                ("folder", "Other"),
                ("content", "# Denied")
            ]);
        Assert.False(
            deniedCreate.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "access_denied",
            deniedCreate.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());

        using var catalog = await CallToolAsync(
            client,
            "list_catalog",
            [("limit", 20)]);
        var titles = catalog.RootElement
            .GetProperty("page")
            .GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("title").GetString())
            .ToArray();
        Assert.Contains("Project Note", titles);
        Assert.DoesNotContain("Other Note", titles);

        using var deniedRead = await CallToolAsync(
            client,
            "read_note",
            [("documentId", other.DocumentId)]);
        Assert.False(deniedRead.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "not_found",
            deniedRead.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());

        await client.DisposeAsync();
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BrokerUnavailableGrantFailureKeepsErrorCode()
    {
        await using var environment = await CreateRealBridgeEnvironmentAsync();
        environment.Fixture.AccessGrantPrompt.Handler = (_, _) =>
            throw new AdminPipeRpcException(
                "approval_broker_unavailable",
                "The interactive Auth Broker is not available.");
        var acceptTask = environment.Server.AcceptOnceAsync();
        await using var client = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                environment.Options.PipeName,
                environment.Options.ClientId,
                environment.Options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal) { "read" },
                environment.Options.AllowedClientPolicy,
                CurrentProcessServicePolicy(environment.Options.OwnerSid)));

        var exception = await Assert.ThrowsAsync<PipeRpcException>(
            () => client.RequestGrantAsync());

        Assert.Equal("approval_broker_unavailable", exception.Code);
        await client.DisposeAsync();
        _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcceptLoopContinuesAfterFailedConnection()
    {
        var handler = new FailingCloseBridgeHandler();
        var pipeName = "CipherVault.Bridge.Test." + Guid.NewGuid().ToString("N");
        var options = CreateBridgeOptions(pipeName, handler);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loopTask = new BridgePipeAcceptLoop(options).RunAsync(stopping.Token);

        await using var first = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                pipeName,
                options.ClientId,
                options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal) { "read" },
                options.AllowedClientPolicy,
                CurrentProcessServicePolicy(options.OwnerSid)));
        await first.DisposeAsync();
        await WaitUntilAsync(
            () => handler.CloseAttempts >= 1,
            TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        await using var second = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                pipeName,
                options.ClientId,
                options.AccessMode,
                new HashSet<string>(StringComparer.Ordinal) { "read" },
                options.AllowedClientPolicy,
                CurrentProcessServicePolicy(options.OwnerSid)));
        await second.DisposeAsync();
        await WaitUntilAsync(
            () => handler.CloseAttempts >= 2,
            TimeSpan.FromSeconds(5));

        stopping.Cancel();
        await loopTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static (
        BridgePipeServer Server,
        BridgePipeServerOptions Options) CreateServer(
        string pipeName,
        IBridgeSessionHandler handler,
        McpAccessMode accessMode = McpAccessMode.ReadOnly)
    {
        var options = CreateBridgeOptions(pipeName, handler, accessMode);
        return (new BridgePipeServer(options), options);
    }

    private static BridgePipeServerOptions CreateBridgeOptions(
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
        return new BridgePipeServerOptions
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
                throw new TimeoutException("Condition was not met before timeout.");
            }
            await Task.Delay(25);
        }
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

    private static async Task<StrictBridgeTestEnvironment> CreateRealBridgeEnvironmentAsync(
        AccessGrantService? grants = null)
    {
        var fixture = await HostTestFixture.CreateAsync();
        var sessions = fixture.App.Services.GetRequiredService<HostSessionCoordinator>();
        grants ??= fixture.App.Services.GetRequiredService<AccessGrantService>();
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

        public Task<BridgeStatusData> GetStatusAsync(
            BridgeProcessSession session,
            AccessGrantContext? grant,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateStatus(session, grant));
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

    private sealed class RevokedGrantBridgeHandler : IBridgeSessionHandler
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
                    session.AccessMode == McpAccessMode.ReadWrite
                        ? "readWrite"
                        : "readOnly",
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
            return Task.FromResult(BridgeToolEnvelope.Error(
                "access_grant_revoked",
                "The Bridge Access Grant was revoked."));
        }

        public Task<BridgeStatusData> GetStatusAsync(
            BridgeProcessSession session,
            AccessGrantContext? grant,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateStatus(session, grant));
        }

        public Task CloseSessionAsync(
            Guid processSessionId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FailingCloseBridgeHandler : IBridgeSessionHandler
    {
        public int CloseAttempts { get; private set; }

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

        public Task<BridgeStatusData> GetStatusAsync(
            BridgeProcessSession session,
            AccessGrantContext? grant,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateStatus(session, grant));
        }

        public Task CloseSessionAsync(
            Guid processSessionId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            CloseAttempts++;
            if (CloseAttempts == 1)
            {
                throw new InvalidOperationException("First close failed.");
            }
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
            if (!AccessGrantPathPolicy.TryParse(
                    request.Scopes,
                    out var requestedScopes,
                    out var requestedPaths))
            {
                throw new AdminPipeRpcException(
                    "invalid_input",
                    "Bridge scope is invalid.");
            }
            var outcome = await grants.RequestAsync(
                session.ClientId,
                session.BridgeInstanceId,
                session.AccessMode,
                requestedScopes,
                requestedPaths,
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
                            approval.PathPolicy,
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
            var validation = grant is null
                ? null
                : grants.PeekBridgeGrant(grant);
            var active =
                validation?.Status == AccessGrantValidationStatus.Valid;
            var scopes = active && validation?.Grant is not null
                ? AccessGrantScopes.ToNames(validation.Grant.Scopes)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()
                : [];
            return Task.FromResult(new BridgeStatusData(
                active,
                session.ClientId,
                session.AccessMode == McpAccessMode.ReadWrite
                    ? "readWrite"
                    : "readOnly",
                session.ProcessSessionId,
                session.BridgeInstanceId,
                session.ExpiresAt,
                scopes,
                active ? validation?.Record?.HardExpiresAt : null));
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

    }

    private static BridgeStatusData CreateStatus(
        BridgeProcessSession session,
        AccessGrantContext? grant)
    {
        return new BridgeStatusData(
            GrantActive: grant is not null,
            session.ClientId,
            session.AccessMode == McpAccessMode.ReadWrite
                ? "readWrite"
                : "readOnly",
            session.ProcessSessionId,
            session.BridgeInstanceId,
            session.ExpiresAt,
            grant is null
                ? []
                : AccessGrantScopes.ToNames(grant.Scopes)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
            GrantExpiresAt: null);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan duration)
        {
            _now += duration;
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
