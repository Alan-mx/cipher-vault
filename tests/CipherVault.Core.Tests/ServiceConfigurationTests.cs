using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class ServiceConfigurationTests
{
    private const string Sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Thumbprint =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void ValidStrictServiceConfigIsParsed()
    {
        using var fixture = new ConfigFixture();
        var dataRoot = Path.Combine(fixture.Root, "data");
        var vaultPath = Path.Combine(dataRoot, "Vault");

        var config = CipherVaultServiceConfiguration.Parse(
            BuildStrictConfig(dataRoot, vaultPath));

        Assert.Equal(3, config.ConfigVersion);
        Assert.Equal("strict", config.SecurityMode);
        Assert.Equal(dataRoot, config.DataRoot);
        Assert.Equal(vaultPath, config.VaultPath);
        Assert.Equal("stdio-bridge", config.Agent?.StrictTransport);
        Assert.False(config.Agent?.CompatibilityHttpEnabled);
        Assert.Single(config.ProcessPolicy!.AllowedSignerThumbprints!);
    }

    [Theory]
    [InlineData("\"configVersion\": 2")]
    [InlineData("\"agent\": {\"strictTransport\":\"stdio-bridge\",\"compatibilityHttpEnabled\":true}")]
    [InlineData("\"processPolicy\": {\"allowedSignerThumbprints\":[],\"requireSha256\":true}")]
    [InlineData("\"pipes\": {\"admin\":\"same\",\"broker\":\"same\",\"bridge\":\"third\",\"rejectRemoteClients\":true}")]
    [InlineData("\"pipes\": {\"admin\":\"admin\",\"broker\":\"broker\",\"bridge\":\"bridge\",\"rejectRemoteClients\":false}")]
    [InlineData("\"token\": \"forbidden\"")]
    public void InvalidStrictServiceConfigIsRejected(string replacement)
    {
        using var fixture = new ConfigFixture();
        var dataRoot = Path.Combine(fixture.Root, "data");
        var vaultPath = Path.Combine(dataRoot, "Vault");
        var json = BuildStrictConfig(dataRoot, vaultPath);
        if (replacement.StartsWith("\"configVersion\"", StringComparison.Ordinal))
        {
            json = json.Replace("\"configVersion\": 3", replacement, StringComparison.Ordinal);
        }
        else if (replacement.StartsWith("\"agent\"", StringComparison.Ordinal))
        {
            json = json.Replace(
                "\"agent\": { \"strictTransport\": \"stdio-bridge\", \"compatibilityHttpEnabled\": false }",
                "\"agent\": " + replacement["\"agent\": ".Length..],
                StringComparison.Ordinal);
        }
        else if (replacement.StartsWith("\"processPolicy\"", StringComparison.Ordinal))
        {
            json = json.Replace(
                "\"processPolicy\": { \"allowedSignerThumbprints\": [\"" + Thumbprint + "\"], \"requireSha256\": true }",
                replacement,
                StringComparison.Ordinal);
        }
        else if (replacement.StartsWith("\"pipes\"", StringComparison.Ordinal))
        {
            json = json.Replace(
                "\"pipes\": { \"admin\": \"CipherVault.Admin.v1\", \"broker\": \"CipherVault.Broker.v1\", \"bridge\": \"CipherVault.Bridge.v1\", \"rejectRemoteClients\": true }",
                replacement,
                StringComparison.Ordinal);
        }
        else
        {
            json = json.Replace(
                "\"configVersion\": 3,",
                "\"configVersion\": 3, " + replacement + ",",
                StringComparison.Ordinal);
        }

        var exception = Assert.Throws<VaultException>(
            () => CipherVaultServiceConfiguration.Parse(json));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void VaultOutsideDataRootIsRejected()
    {
        using var fixture = new ConfigFixture();
        var json = BuildStrictConfig(
            Path.Combine(fixture.Root, "data"),
            Path.Combine(fixture.Root, "outside", "Vault"));

        var exception = Assert.Throws<VaultException>(
            () => CipherVaultServiceConfiguration.Parse(json));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void ServicePathsUseProgramDataAndProgramFilesLayout()
    {
        var paths = CipherVaultServicePaths.Create(
            @"C:\Program Files",
            @"C:\ProgramData",
            @"C:\Users\owner\AppData\Local");

        Assert.Equal(@"C:\Program Files\CipherVault", paths.InstallRoot);
        Assert.Equal(@"C:\ProgramData\CipherVault", paths.DataRoot);
        Assert.EndsWith(
            Path.Combine("CipherVault", "Vault", "vault.mode"),
            paths.ModePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("CipherVault", "client.json"),
            paths.ClientConfigPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidInstallManifestIsAccepted()
    {
        var manifest = new CipherVaultInstallPublicManifest
        {
            Format = CipherVaultServiceConfiguration.InstallManifestFormat,
            FormatVersion = 1,
            InstallId = Guid.NewGuid(),
            ProductVersion = "1.0.0",
            ServiceName = "CipherVaultHost",
            ServiceAccount = "NT SERVICE\\CipherVaultHost",
            ServiceSid = "S-1-5-80-1",
            OwnerSid = "S-1-5-21-1",
            Pipes = ValidPipes(),
            AllowedServerImages =
            [
                new CipherVaultImagePolicyFile
                {
                    Kind = "host",
                    Path = @"C:\Program Files\CipherVault\CipherVault.Host.Service.exe",
                    Sha256 = Sha256,
                    SignerThumbprint = Thumbprint
                }
            ],
            AllowedClientImages =
            [
                new CipherVaultImagePolicyFile
                {
                    Kind = "ui",
                    Path = @"C:\Program Files\CipherVault\CipherVault.Ui.Wpf.exe",
                    Sha256 = Sha256,
                    SignerThumbprint = Thumbprint
                }
            ]
        };

        CipherVaultServiceConfiguration.Validate(manifest);
    }

    [Fact]
    public void ValidVaultModeManifestIsAccepted()
    {
        CipherVaultServiceConfiguration.Validate(
            new CipherVaultVaultModeManifest
            {
                Format = CipherVaultServiceConfiguration.VaultModeFormat,
                FormatVersion = 1,
                Mode = "strict",
                OwnerSid = "S-1-5-21-1",
                InstallId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    private static string BuildStrictConfig(string dataRoot, string vaultPath)
    {
        var escapedDataRoot = dataRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        var escapedVaultPath = vaultPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        return $$"""
            {
              "configVersion": 3,
              "securityMode": "strict",
              "ownerSid": "S-1-5-21-1",
              "serviceAccount": "NT SERVICE\\CipherVaultHost",
              "vaultPath": "{{escapedVaultPath}}",
              "dataRoot": "{{escapedDataRoot}}",
              "host": {
                "mcpPath": "/mcp",
                "maxRequestBodyBytes": 4194304,
                "allowedOrigins": []
              },
              "pipes": { "admin": "CipherVault.Admin.v1", "broker": "CipherVault.Broker.v1", "bridge": "CipherVault.Bridge.v1", "rejectRemoteClients": true },
              "processPolicy": { "allowedSignerThumbprints": ["{{Thumbprint}}"], "requireSha256": true },
              "agent": { "strictTransport": "stdio-bridge", "compatibilityHttpEnabled": false }
            }
            """;
    }

    private static CipherVaultPipeConfigFile ValidPipes()
    {
        return new CipherVaultPipeConfigFile
        {
            Admin = "CipherVault.Admin.v1",
            Broker = "CipherVault.Broker.v1",
            Bridge = "CipherVault.Bridge.v1",
            RejectRemoteClients = true
        };
    }

    private sealed class ConfigFixture : IDisposable
    {
        public ConfigFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-service-config-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
