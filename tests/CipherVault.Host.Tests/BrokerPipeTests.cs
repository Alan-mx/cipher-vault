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

public sealed class BrokerPipeTests
{
    [Fact]
    public async Task BrokerReturnsCredentialForServiceRequest()
    {
        var pipeName = "CipherVault.Broker.Test." + Guid.NewGuid().ToString("N");
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
        var brokerPolicy = Policy(
            VerifiedProcessKind.AuthBroker,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        await using var server = new BrokerPipeServer(
            new BrokerPipeServerOptions(
                pipeName,
                PipeSecurityFactory.Create(
                    [currentUser],
                    includeAdministrators: false,
                    includeSystem: false),
                Guid.NewGuid().ToString("D"),
                "1.0.0-test",
                serviceIdentity,
                [brokerPolicy],
                currentUser.Value,
                currentUser.Value,
                TimeSpan.FromMinutes(5),
                RequireInteractiveSession: false));
        var connected = new TaskCompletionSource<BrokerPipeConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = server.AcceptOnceAsync(
            connection => connected.TrySetResult(connection),
            _ => { },
            timeout.Token);

        await using var client = await BrokerPipeClientConnection.ConnectAsync(
            new BrokerPipeClientOptions(
                pipeName,
                brokerPolicy,
                servicePolicy));
        var readTask = client.RunAsync(
            (request, _) =>
            {
                Assert.Equal("password", request.Preference);
                return Task.FromResult(new BrokerCredentialPromptResult(
                    request.RequestId,
                    request.NonceB64Url,
                    Confirmed: true,
                    Credential: "test-password",
                    CredentialType: "password",
                    DenialReason: null));
            },
            timeout.Token);
        var connection = await connected.Task.WaitAsync(timeout.Token);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var request = new BrokerCredentialPromptRequest(
            Guid.NewGuid(),
            PipeNonce.Create(),
            "password",
            "Unlock",
            expiresAt);

        var result = await connection.CallAsync<
            BrokerCredentialPromptRequest,
            BrokerCredentialPromptResult>(
            "broker.credential.request",
            request,
            expiresAt,
            timeout.Token);

        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(request.NonceB64Url, result.NonceB64Url);
        Assert.Equal("test-password", result.Credential);
        Assert.Equal("credential-prompt.v1", Assert.Single(
            client.Capabilities.Features));

        timeout.Cancel();
        await readTask;
        await client.DisposeAsync();
        await acceptTask;
    }

    [Fact]
    public void RegistryReportsBrokerUnavailable()
    {
        var registry = new BrokerConnectionRegistry();

        Assert.False(registry.IsAvailable);
        var exception = Assert.Throws<AdminPipeRpcException>(
            registry.GetRequired);
        Assert.Equal("approval_broker_unavailable", exception.Code);
    }

    [Fact]
    public void ClientPolicyFactoryBuildsBrokerAndHostPolicies()
    {
        const string sha256 =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string thumbprint =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var manifest = new CipherVaultInstallPublicManifest
        {
            Format = CipherVaultServiceConfiguration.InstallManifestFormat,
            FormatVersion =
                CipherVaultServiceConfiguration.InstallManifestFormatVersion,
            InstallId = Guid.NewGuid(),
            ProductVersion = "1.0.0",
            ServiceName = "CipherVaultHost",
            ServiceAccount = @"NT SERVICE\CipherVaultHost",
            ServiceSid = "S-1-5-80-1",
            OwnerSid = "S-1-5-21-1",
            Pipes = new CipherVaultPipeConfigFile
            {
                Admin = "CipherVault.Admin.v1",
                Broker = "CipherVault.Broker.v1",
                Bridge = "CipherVault.Bridge.v1",
                RejectRemoteClients = true
            },
            AllowedServerImages =
            [
                Image(
                    "HostService",
                    @"C:\Program Files\CipherVault\CipherVault.Host.exe",
                    sha256,
                    thumbprint)
            ],
            AllowedClientImages =
            [
                Image(
                    "AuthBroker",
                    @"C:\Program Files\CipherVault\CipherVault.Auth.Broker.exe",
                    sha256,
                    thumbprint)
            ]
        };

        var options = StrictServiceClientPolicyFactory.CreateBrokerOptions(
            manifest,
            manifest.Pipes.Broker!,
            manifest.OwnerSid!,
            ownerSessionId: 7);

        Assert.Equal(VerifiedProcessKind.AuthBroker, options.ClientPolicy.ExpectedKind);
        Assert.Equal(7u, options.ClientPolicy.ExpectedSessionId);
        Assert.Equal(VerifiedProcessKind.HostService, options.ServerPolicy.ExpectedKind);
        Assert.Equal("S-1-5-80-1", options.ServerPolicy.ExpectedUserSid);
        Assert.Equal(0u, options.ServerPolicy.ExpectedSessionId);
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

    private static CipherVaultImagePolicyFile Image(
        string kind,
        string path,
        string sha256,
        string thumbprint)
    {
        return new CipherVaultImagePolicyFile
        {
            Kind = kind,
            Path = path,
            Sha256 = sha256,
            SignerThumbprint = thumbprint,
            SignerSubject = "CN=CipherVault Test"
        };
    }
}
