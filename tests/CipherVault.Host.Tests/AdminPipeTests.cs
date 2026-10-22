using System.Diagnostics;
using System.Security.Principal;
using CipherVault.Core.Documents;
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
                (_, method, _, _) => method == "admin.catalog.list"
                    ? Task.FromResult<object>(
                        new VaultCatalogPage([], "catalog-next"))
                    : Task.FromException<object>(
                        new InvalidOperationException(
                            $"Unexpected method: {method}"))));
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

        await client.DisposeAsync();
        var session = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(session);
        Assert.Equal(
            AdminSessionState.Revoked,
            Assert.Single(server.Sessions.List()).State);
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
}
