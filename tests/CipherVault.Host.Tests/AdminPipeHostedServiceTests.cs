using System.IO;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Host.Contracts;
using CipherVault.Host.Service;

namespace CipherVault.Host.Tests;

public sealed class AdminPipeHostedServiceTests
{
    private const string Sha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Thumbprint =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

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
        }

        public string Root { get; }

        public string InstallRoot { get; }

        public string HostPath { get; }

        public string UiPath { get; }

        public string CliPath { get; }

        public CipherVaultServiceConfigFile CreateConfiguration(
            bool includeUi = true,
            bool includeCli = true)
        {
            var images = new List<CipherVaultImagePolicyFile>
            {
                Image("HostService", HostPath)
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
