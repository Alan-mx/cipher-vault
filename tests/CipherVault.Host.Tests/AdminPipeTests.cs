using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;

namespace CipherVault.Host.Tests;

public sealed class AdminPipeTests
{
    [Theory]
    [InlineData(VaultErrorCode.Conflict, "revision_conflict")]
    [InlineData(VaultErrorCode.NotFound, "not_found")]
    [InlineData(VaultErrorCode.InvalidCursor, "invalid_cursor")]
    [InlineData(VaultErrorCode.LinkIndexTooLarge, "link_index_too_large")]
    public void AdminPipeMapsDomainErrors(
        VaultErrorCode code,
        string expected)
    {
        var method = typeof(AdminPipeServer).GetMethod(
            "ToErrorCode",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Admin error mapping method was not found.");

        Assert.Equal(
            expected,
            method.Invoke(
                obj: null,
                [new VaultException(code, "test")]));
    }

    [Fact]
    public async Task NamedPipeHostVaultClientReturnsStatusFromAdminPipe()
    {
        var pipeName = "CipherVault.Admin.ClientTest." + Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var clientPolicy = Policy(
            VerifiedProcessKind.AdminUi,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var expectedSession = new HostSessionStatusData(
            "locked",
            SessionId: null,
            StartedAt: null,
            LastUserActivityAt: null,
            IdleExpiresAt: null,
            HardExpiresAt: null,
            LockReason: "startup",
            ActiveLeases: 0,
            ConnectedClients: 1,
            RecoveryVerified: true);
        var security = PipeSecurityFactory.Create(
            [currentUser],
            includeAdministrators: false,
            includeSystem: false);
        await using var server = new AdminPipeServer(
            new AdminPipeServerOptions(
                pipeName,
                security,
                Guid.NewGuid().ToString("D"),
                "strict",
                "1.0.0-test",
                serviceIdentity,
                [clientPolicy],
                currentUser.Value,
                currentUser.Value,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(4),
                (_, _) => Task.FromResult(
                    new AdminPipeStatusData(
                        "1.0.0-test",
                        PipeProtocol.Version,
                        "strict",
                        BrokerAvailable: true,
                        expectedSession)),
                (_, reason, _) => Task.FromResult(
                    expectedSession with { LockReason = reason }),
                (_, _, _) => Task.FromException<HostSessionStatusData>(
                    new AdminPipeRpcException(
                        "approval_broker_unavailable",
                        "Broker unavailable.")),
                (_, _, _) => Task.FromException<HostInitializeData>(
                    new AdminPipeRpcException(
                        "approval_broker_unavailable",
                        "Broker unavailable.")),
                (_, method, _, _) => method switch
                {
                    "admin.catalog.list" => Task.FromResult<object>(
                        new VaultCatalogPage([], "catalog-next")),
                    "admin.maintenance.rebuild_links" => Task.FromResult<object>(
                        new VaultLinkRebuildResult(2, 3, true)),
                    _ => Task.FromException<object>(
                        new InvalidOperationException(
                            $"Unexpected method: {method}"))
                }));
        var acceptTask = server.AcceptOnceAsync();

        await using var client = await NamedPipeHostVaultClient.CreateAsync(
            new AdminPipeClientOptions(
                pipeName,
                clientPolicy,
                servicePolicy));
        var status = await client.GetStatusAsync();

        Assert.Equal(VaultSessionState.Locked, status.State);
        Assert.Null(status.SessionId);
        Assert.Equal("startup", status.LockReason);
        Assert.True(status.RecoveryVerified);
        Assert.True(status.BrokerAvailable);
        var locked = await client.LockAsync("test-lock");
        Assert.Equal("test-lock", locked.LockReason);
        var unlockException = await Assert.ThrowsAsync<PipeRpcException>(
            () => client.UnlockAsync(
                vaultPath: @"C:\Vault",
                credential: string.Empty,
                credentialType: VaultCredentialType.Password));
        Assert.Equal(
            "approval_broker_unavailable",
            unlockException.Code);
        var catalog = await client.ListCatalogAsync(
            folder: null,
            cursor: null,
            limit: 100);
        Assert.Empty(catalog.Items);
        Assert.Equal("catalog-next", catalog.NextCursor);
        var rebuilt = await client.RebuildLinkIndexAsync();
        Assert.Equal(2, rebuilt.DocumentsIndexed);
        Assert.Equal(3, rebuilt.LinksIndexed);
        Assert.True(rebuilt.AuditRecorded);

        await client.DisposeAsync();
        var session = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(session);
        Assert.Equal(
            AdminSessionState.Revoked,
            Assert.Single(server.Sessions.List()).State);
    }

    [Fact]
    public async Task LockRevokesAdminSessionAndStatusReconnects()
    {
        var pipeName = "CipherVault.Admin.ReconnectTest." +
            Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var clientPolicy = Policy(
            VerifiedProcessKind.AdminUi,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var status = new HostSessionStatusData(
            "locked",
            SessionId: null,
            StartedAt: null,
            LastUserActivityAt: null,
            IdleExpiresAt: null,
            HardExpiresAt: null,
            LockReason: "startup",
            ActiveLeases: 0,
            ConnectedClients: 1,
            RecoveryVerified: true);
        var adminSessions = new AdminSessionStore(
            idleLifetime: TimeSpan.FromMinutes(15),
            hardLifetime: TimeSpan.FromHours(4));
        var options = new AdminPipeServerOptions(
            pipeName,
            PipeSecurityFactory.Create(
                [currentUser],
                includeAdministrators: false,
                includeSystem: false),
            Guid.NewGuid().ToString("D"),
            "strict",
            "1.0.0-test",
            serviceIdentity,
            [clientPolicy],
            currentUser.Value,
            currentUser.Value,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(4),
            StatusProvider: (_, _) => Task.FromResult(
                new AdminPipeStatusData(
                    "1.0.0-test",
                    PipeProtocol.Version,
                    "strict",
                    BrokerAvailable: true,
                    status)),
            LockProvider: (_, reason, _) =>
            {
                status = status with { LockReason = reason };
                adminSessions.RevokeAll(reason);
                return Task.FromResult(status);
            },
            AdminSessions: adminSessions);
        using var stopping = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        var loopTask = StrictPipeAcceptLoop.RunAsync(
            async token =>
            {
                await using var server = new AdminPipeServer(options);
                _ = await server.AcceptOnceAsync(token);
            },
            TimeSpan.FromMilliseconds(50),
            stopping.Token);

        await using var client = await NamedPipeHostVaultClient.CreateAsync(
            new AdminPipeClientOptions(
                pipeName,
                clientPolicy,
                servicePolicy));
        var before = await client.GetStatusAsync();
        Assert.Equal(VaultSessionState.Locked, before.State);

        var locked = await client.LockAsync("test-lock");
        Assert.Equal("test-lock", locked.LockReason);

        var after = await client.GetStatusAsync();
        Assert.Equal(VaultSessionState.Locked, after.State);
        Assert.Equal("test-lock", after.LockReason);

        stopping.Cancel();
        await loopTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AdminPipeHandshakeReturnsStatusAndRevokesOnDisconnect()
    {
        var pipeName = "CipherVault.Admin.Test." + Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var clientPolicy = Policy(
            VerifiedProcessKind.AdminUi,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var expectedStatus = new AdminPipeStatusData(
            "1.0.0-test",
            PipeProtocol.Version,
            "strict",
            BrokerAvailable: true,
            new HostSessionStatusData(
                "locked",
                SessionId: null,
                StartedAt: null,
                LastUserActivityAt: null,
                IdleExpiresAt: null,
                HardExpiresAt: null,
                LockReason: null,
                ActiveLeases: 0,
                ConnectedClients: 0,
                RecoveryVerified: false));
        var security = PipeSecurityFactory.Create(
            [currentUser],
            includeAdministrators: false,
            includeSystem: false);
        await using var server = new AdminPipeServer(
            new AdminPipeServerOptions(
                pipeName,
                security,
                Guid.NewGuid().ToString("D"),
                "strict",
                "1.0.0-test",
                serviceIdentity,
                [clientPolicy],
                currentUser.Value,
                currentUser.Value,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(4),
                (_, _) => Task.FromResult(expectedStatus)));
        var acceptTask = server.AcceptOnceAsync();

        await using var client = await AdminPipeClientConnection.ConnectAsync(
            new AdminPipeClientOptions(
                pipeName,
                clientPolicy,
                servicePolicy));
        var status = await client.GetStatusAsync();
        Assert.Equal(expectedStatus, status);

        var sessionId = client.AdminSessionId;
        await client.DisposeAsync();
        var session = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(session);
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(
            AdminSessionState.Revoked,
            Assert.Single(server.Sessions.List()).State);
    }

    [Fact]
    public async Task AcceptLoopContinuesAfterFailedHandshake()
    {
        var pipeName = "CipherVault.Admin.LoopTest." + Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var clientPolicy = Policy(
            VerifiedProcessKind.AdminUi,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var expectedStatus = new AdminPipeStatusData(
            "1.0.0-test",
            PipeProtocol.Version,
            "strict",
            BrokerAvailable: false,
            new HostSessionStatusData(
                "locked",
                SessionId: null,
                StartedAt: null,
                LastUserActivityAt: null,
                IdleExpiresAt: null,
                HardExpiresAt: null,
                LockReason: null,
                ActiveLeases: 0,
                ConnectedClients: 0,
                RecoveryVerified: false));
        var options = new AdminPipeServerOptions(
            pipeName,
            PipeSecurityFactory.Create(
                [currentUser],
                includeAdministrators: false,
                includeSystem: false),
            Guid.NewGuid().ToString("D"),
            "strict",
            "1.0.0-test",
            serviceIdentity,
            [clientPolicy],
            currentUser.Value,
            currentUser.Value,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(4),
            (_, _) => Task.FromResult(expectedStatus));
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var attempts = 0;
        var loopTask = StrictPipeAcceptLoop.RunAsync(
            async token =>
            {
                Interlocked.Increment(ref attempts);
                await using var server = new AdminPipeServer(options);
                _ = await server.AcceptOnceAsync(token);
            },
            TimeSpan.FromMilliseconds(50),
            stopping.Token);

        await WriteInvalidHandshakeAsync(pipeName);
        await WaitUntilAsync(
            () => Volatile.Read(ref attempts) >= 1,
            TimeSpan.FromSeconds(5));
        await Task.Delay(150);

        await using var client = await NamedPipeHostVaultClient.CreateAsync(
            new AdminPipeClientOptions(
                pipeName,
                clientPolicy,
                servicePolicy));
        var status = await client.GetStatusAsync();

        Assert.Equal(VaultSessionState.Locked, status.State);
        Assert.Null(status.SessionId);
        Assert.False(status.BrokerAvailable);
        Assert.True(Volatile.Read(ref attempts) >= 2);
        stopping.Cancel();
        await loopTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentClientsRemainIndependentWhenOneDisconnects()
    {
        var pipeName = "CipherVault.Admin.ConcurrentTest." +
            Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var clientPolicy = Policy(
            VerifiedProcessKind.AdminUi,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var adminSessions = new AdminSessionStore(
            idleLifetime: TimeSpan.FromMinutes(15),
            hardLifetime: TimeSpan.FromHours(4));
        var connectedClients = 0;
        var status = new HostSessionStatusData(
            "locked",
            SessionId: null,
            StartedAt: null,
            LastUserActivityAt: null,
            IdleExpiresAt: null,
            HardExpiresAt: null,
            LockReason: "startup",
            ActiveLeases: 0,
            ConnectedClients: 2,
            RecoveryVerified: false);
        var options = new AdminPipeServerOptions(
            pipeName,
            PipeSecurityFactory.Create(
                [currentUser],
                includeAdministrators: false,
                includeSystem: false),
            Guid.NewGuid().ToString("D"),
            "strict",
            "1.0.0-test",
            serviceIdentity,
            [clientPolicy],
            currentUser.Value,
            currentUser.Value,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(4),
            StatusProvider: (_, _) => Task.FromResult(
                new AdminPipeStatusData(
                    "1.0.0-test",
                    PipeProtocol.Version,
                    "strict",
                    BrokerAvailable: false,
                    status)),
            AdminSessions: adminSessions,
            ConnectedClientLease: () =>
            {
                Interlocked.Increment(ref connectedClients);
                return new ActionDisposable(
                    () => Interlocked.Decrement(ref connectedClients));
            });
        using var stopping = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        await using var firstServer = new AdminPipeServer(
            options,
            maxInstances: 2,
            isFirstInstance: true);
        await using var secondServer = new AdminPipeServer(
            options,
            maxInstances: 2,
            isFirstInstance: false);
        var firstAccept = firstServer.AcceptOnceAsync(stopping.Token);
        var secondAccept = secondServer.AcceptOnceAsync(stopping.Token);
        var clientOptions = new AdminPipeClientOptions(
            pipeName,
            clientPolicy,
            servicePolicy);

        await using var firstClient =
            await AdminPipeClientConnection.ConnectAsync(clientOptions);
        await using var secondClient =
            await AdminPipeClientConnection.ConnectAsync(clientOptions);
        var firstStatus = await firstClient.GetStatusAsync();
        var secondStatus = await secondClient.GetStatusAsync();

        Assert.Equal("locked", firstStatus.Session.State);
        Assert.Equal("locked", secondStatus.Session.State);
        Assert.Equal(2, adminSessions.List().Count);
        Assert.Equal(2, Volatile.Read(ref connectedClients));
        await secondClient.DisposeAsync();
        await WaitUntilAsync(
            () =>
                adminSessions.List()
                    .Single(session =>
                        session.SessionId == secondClient.AdminSessionId)
                    .State == AdminSessionState.Revoked &&
                Volatile.Read(ref connectedClients) == 1,
            TimeSpan.FromSeconds(5));
        var completedAccept = await Task.WhenAny(
            firstAccept,
            secondAccept);
        await completedAccept;
        var completedServer = ReferenceEquals(completedAccept, firstAccept)
            ? firstServer
            : secondServer;
        await completedServer.DisposeAsync();

        var firstStatusAfterDisconnect =
            await firstClient.GetStatusAsync();
        Assert.Equal(
            "locked",
            firstStatusAfterDisconnect.Session.State);
        Assert.True(adminSessions.TryGetActive(
            firstClient.AdminSessionId,
            out _));
        Assert.Equal(1, Volatile.Read(ref connectedClients));

        await firstClient.DisposeAsync();
        var remainingAccept = ReferenceEquals(
            completedAccept,
            firstAccept)
            ? secondAccept
            : firstAccept;
        await remainingAccept.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Volatile.Read(ref connectedClients));
    }

    private static async Task WriteInvalidHandshakeAsync(string pipeName)
    {
        await using var raw = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await raw.ConnectAsync(5000);
        var payload = Encoding.UTF8.GetBytes("{}");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await raw.WriteAsync(length);
        await raw.WriteAsync(payload);
        await raw.FlushAsync();
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
                    "Expected Admin pipe accept attempt did not happen.");
            }
            await Task.Delay(25);
        }
    }

    private static ProcessVerificationPolicy Policy(
        VerifiedProcessKind kind,
        string userSid,
        uint sessionId,
        string imagePath,
        string imageHash)
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
                    imageHash,
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

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
