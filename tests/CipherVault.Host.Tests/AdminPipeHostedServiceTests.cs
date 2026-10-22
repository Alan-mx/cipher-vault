using System.IO;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Service;
using CipherVault.Host.Session;

namespace CipherVault.Host.Tests;

public sealed class AdminPipeHostedServiceTests
{
    private const string Sha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Thumbprint =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task StatusProviderReturnsCurrentVaultSession()
    {
        var provider = new StrictAdminStatusProvider(
            _ => Task.FromResult(new HostSessionSnapshot(
                VaultSessionState.Unlocked,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(10),
                DateTimeOffset.UtcNow.AddHours(4),
                LockReason: null,
                ActiveLeases: 2,
                ConnectedClients: 1,
                RecoveryVerified: true)));

        var status = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal("strict", status.SecurityMode);
        Assert.False(status.BrokerAvailable);
        Assert.Equal("unlocked", status.Session.State);
        Assert.Equal(2, status.Session.ActiveLeases);
        Assert.True(status.Session.RecoveryVerified);
    }

    [Fact]
    public async Task LockProviderDelegatesReasonAndReturnsLockedSession()
    {
        string? receivedReason = null;
        var provider = new StrictAdminLockProvider(
            (reason, _) =>
            {
                receivedReason = reason;
                return Task.FromResult(new HostSessionSnapshot(
                    VaultSessionState.Locked,
                    SessionId: null,
                    StartedAt: null,
                    LastUserActivityAt: null,
                    IdleExpiresAt: null,
                    HardExpiresAt: null,
                    LockReason: reason,
                    ActiveLeases: 0,
                    ConnectedClients: 1,
                    RecoveryVerified: false));
            });

        var status = await provider.LockAsync(
            "test-lock",
            CancellationToken.None);

        Assert.Equal("test-lock", receivedReason);
        Assert.Equal("locked", status.State);
        Assert.Equal("test-lock", status.LockReason);
    }

    [Fact]
    public async Task UnlockAndInitializeProvidersDelegateToSessionCoordinator()
    {
        var preference = HostCredentialPreference.Any;
        var initializePath = string.Empty;
        var unlockProvider = new StrictAdminUnlockProvider(
            (requestedPreference, _) =>
            {
                preference = requestedPreference;
                return Task.FromResult(new HostSessionSnapshot(
                    VaultSessionState.Unlocked,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    DateTimeOffset.UtcNow.AddHours(4),
                    LockReason: null,
                    ActiveLeases: 0,
                    ConnectedClients: 1,
                    RecoveryVerified: true));
            });
        var initializeProvider = new StrictAdminInitializeProvider(
            (path, _) =>
            {
                initializePath = path;
                return Task.FromResult(new HostInitializeData(
                    Guid.NewGuid(),
                    path,
                    "RECOVERY-KEY",
                    DateTimeOffset.UtcNow));
            });

        var status = await unlockProvider.UnlockAsync(
            HostCredentialPreference.RecoveryKey,
            CancellationToken.None);
        var initialized = await initializeProvider.InitializeAsync(
            @"C:\ProgramData\CipherVault\Vault",
            CancellationToken.None);

        Assert.Equal(HostCredentialPreference.RecoveryKey, preference);
        Assert.Equal("unlocked", status.State);
        Assert.Equal(
            @"C:\ProgramData\CipherVault\Vault",
            initializePath);
        Assert.Equal(initializePath, initialized.VaultPath);
    }

    [Fact]
    public void PoliciesIncludeServiceAndBothAdminClientKinds()
    {
        using var fixture = new PolicyFixture();
        var configuration = fixture.CreateConfiguration();

        var service = AdminPipeHostedService.CreateServicePolicy(
            configuration,
            "S-1-5-80-1",
            fixture.InstallRoot);
        var clients = AdminPipeHostedService.CreateClientPolicies(
            configuration,
            "S-1-5-21-1",
            fixture.InstallRoot);

        Assert.Equal(VerifiedProcessKind.HostService, service.ExpectedKind);
        Assert.Equal("S-1-5-80-1", service.ExpectedUserSid);
        Assert.Equal(0u, service.ExpectedSessionId);
        Assert.Single(service.AllowedImages);
        Assert.Equal(
            [
                VerifiedProcessKind.AdminUi,
                VerifiedProcessKind.AdminCli
            ],
            clients.Select(policy => policy.ExpectedKind));
        Assert.All(clients, policy =>
        {
            Assert.Equal("S-1-5-21-1", policy.ExpectedUserSid);
            Assert.True(policy.RequireSignature);
        });
        var broker = AdminPipeHostedService.CreateBrokerPolicy(
            configuration,
            "S-1-5-21-1",
            fixture.InstallRoot);
        Assert.Equal(VerifiedProcessKind.AuthBroker, broker.ExpectedKind);
        Assert.Equal("S-1-5-21-1", broker.ExpectedUserSid);
        Assert.Null(broker.ExpectedSessionId);
        Assert.True(broker.RequireSignature);
        Assert.Single(broker.AllowedImages);
        var bridge = AdminPipeHostedService.CreateBridgePolicy(
            configuration,
            "S-1-5-21-1",
            fixture.InstallRoot);
        Assert.Equal(VerifiedProcessKind.McpBridge, bridge.ExpectedKind);
        Assert.Equal("S-1-5-21-1", bridge.ExpectedUserSid);
        Assert.Null(bridge.ExpectedSessionId);
        Assert.True(bridge.RequireSignature);
        Assert.Single(bridge.AllowedImages);
    }

    [Fact]
    public void ClientPoliciesRequireAnAdminBinary()
    {
        using var fixture = new PolicyFixture();
        var configuration = fixture.CreateConfiguration(
            includeUi: false,
            includeCli: false);

        var exception = Assert.Throws<VaultException>(
            () => AdminPipeHostedService.CreateClientPolicies(
                configuration,
                "S-1-5-21-1",
                fixture.InstallRoot));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void PolicyRejectsImageOutsideInstallRoot()
    {
        using var fixture = new PolicyFixture();
        var configuration = fixture.CreateConfiguration();
        var outsidePath = Path.Combine(fixture.Root, "outside.exe");
        var originalImage = configuration.ProcessPolicy!.AllowedImages![0];
        configuration = configuration with
        {
            ProcessPolicy = new CipherVaultProcessPolicyConfigFile
            {
                AllowedSignerThumbprints = [Thumbprint],
                AllowedImages =
                [
                    new CipherVaultImagePolicyFile
                    {
                        Kind = originalImage.Kind,
                        Path = outsidePath,
                        Sha256 = originalImage.Sha256,
                        SignerThumbprint = originalImage.SignerThumbprint,
                        SignerSubject = originalImage.SignerSubject
                    },
                    .. configuration.ProcessPolicy.AllowedImages.Skip(1)
                ],
                RequireSha256 = true
            }
        };
        File.WriteAllBytes(
            outsidePath,
            [1]);

        var exception = Assert.Throws<VaultException>(
            () => AdminPipeHostedService.CreateServicePolicy(
                configuration,
                "S-1-5-80-1",
                fixture.InstallRoot));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    private sealed class PolicyFixture : IDisposable
    {
        public PolicyFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-admin-policy-{Guid.NewGuid():N}");
            InstallRoot = Path.Combine(Root, "ProgramFiles", "CipherVault");
            Directory.CreateDirectory(InstallRoot);
            HostPath = CreateImage("CipherVault.Host.exe");
            UiPath = CreateImage("CipherVault.Ui.Wpf.exe");
            CliPath = CreateImage("CipherVault.Cli.exe");
            BrokerPath = CreateImage("CipherVault.Auth.Broker.exe");
            McpPath = CreateImage("CipherVault.Mcp.exe");
        }

        public string Root { get; }

        public string InstallRoot { get; }

        public string HostPath { get; }

        public string UiPath { get; }

        public string CliPath { get; }
        public string BrokerPath { get; }
        public string McpPath { get; }

        public CipherVaultServiceConfigFile CreateConfiguration(
            bool includeUi = true,
            bool includeCli = true)
        {
            var images = new List<CipherVaultImagePolicyFile>
            {
                Image("HostService", HostPath),
                Image("AuthBroker", BrokerPath),
                Image("McpBridge", McpPath)
            };
            if (includeUi)
            {
                images.Add(Image("AdminUi", UiPath));
            }
            if (includeCli)
            {
                images.Add(Image("AdminCli", CliPath));
            }
            return new CipherVaultServiceConfigFile
            {
                ConfigVersion =
                    CipherVaultServiceConfiguration.CurrentConfigVersion,
                SecurityMode =
                    CipherVaultServiceConfiguration.StrictModeValue,
                OwnerSid = "S-1-5-21-1",
                ServiceAccount = @"NT SERVICE\CipherVaultHost",
                VaultPath = Path.Combine(Root, "ProgramData", "CipherVault", "Vault"),
                DataRoot = Path.Combine(Root, "ProgramData", "CipherVault"),
                Pipes = new CipherVaultPipeConfigFile
                {
                    Admin = "CipherVault.Admin.v1",
                    Broker = "CipherVault.Broker.v1",
                    Bridge = "CipherVault.Bridge.v1",
                    RejectRemoteClients = true
                },
                ProcessPolicy = new CipherVaultProcessPolicyConfigFile
                {
                    AllowedSignerThumbprints = [Thumbprint],
                    AllowedImages = images,
                    RequireSha256 = true
                }
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private string CreateImage(string fileName)
        {
            var path = Path.Combine(InstallRoot, fileName);
            File.WriteAllBytes(path, [1, 2, 3]);
            return path;
        }

        private static CipherVaultImagePolicyFile Image(
            string kind,
            string path)
        {
            return new CipherVaultImagePolicyFile
            {
                Kind = kind,
                Path = path,
                Sha256 = Sha256,
                SignerThumbprint = Thumbprint,
                SignerSubject = "CN=CipherVault Test"
            };
        }
    }
}
