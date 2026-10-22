using System.IO;
using CipherVault.Host.ServiceManagement;

namespace CipherVault.Host.Tests;

public sealed class HostServiceStatusTests
{
    [Fact]
    public void StatusIncludesManifestAndScmState()
    {
        using var fixture = new StatusFixture();
        var status = HostServiceStatusReader.Read(
            "CipherVaultHost",
            fixture.ManifestPath,
            new StubServiceQuery(
                new WindowsServiceSnapshot(true, "running", "Automatic")));

        Assert.True(status.Installed);
        Assert.Equal("running", status.State);
        Assert.True(status.InstallManifestPresent);
        Assert.Equal("1.0.0", status.ProductVersion);
        Assert.Null(status.InstallManifestError);
    }

    [Fact]
    public void StatusReportsInvalidManifestWithoutMistakingItForRunning()
    {
        using var fixture = new StatusFixture();
        File.WriteAllText(fixture.ManifestPath, """{"format":"invalid"}""");

        var status = HostServiceStatusReader.Read(
            "CipherVaultHost",
            fixture.ManifestPath,
            new StubServiceQuery(
                new WindowsServiceSnapshot(true, "stopped", "Automatic")));

        Assert.True(status.Installed);
        Assert.Equal("stopped", status.State);
        Assert.True(status.InstallManifestPresent);
        Assert.NotNull(status.InstallManifestError);
    }

    [Fact]
    public void StatusReportsNotInstalledWithoutManifest()
    {
        using var fixture = new StatusFixture(createManifest: false);
        var status = HostServiceStatusReader.Read(
            "CipherVaultHost",
            fixture.ManifestPath,
            new StubServiceQuery(
                new WindowsServiceSnapshot(false, "notInstalled", null)));

        Assert.False(status.Installed);
        Assert.False(status.InstallManifestPresent);
        Assert.Null(status.StartMode);
    }

    private sealed class StubServiceQuery(WindowsServiceSnapshot snapshot)
        : IWindowsServiceQuery
    {
        public WindowsServiceSnapshot GetStatus(string serviceName)
        {
            Assert.Equal("CipherVaultHost", serviceName);
            return snapshot;
        }
    }

    private sealed class StatusFixture : IDisposable
    {
        private const string Sha256 =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Thumbprint =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        public StatusFixture(bool createManifest = true)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-service-status-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            ManifestPath = Path.Combine(Root, "install-public.json");
            if (createManifest)
            {
                var imagePath = Path.Combine(
                    Root,
                    "CipherVault.Host.exe").Replace(
                    "\\",
                    "\\\\",
                    StringComparison.Ordinal);
                File.WriteAllText(
                    ManifestPath,
                    $$"""
                    {
                      "format": "cipher-vault-install",
                      "formatVersion": 1,
                      "installId": "5e9784db-3fe8-4e2d-84ca-fd281e6d2685",
                      "productVersion": "1.0.0",
                      "serviceName": "CipherVaultHost",
                      "serviceAccount": "NT SERVICE\\CipherVaultHost",
                      "serviceSid": "S-1-5-80-1",
                      "ownerSid": "S-1-5-21-1",
                      "pipes": { "admin": "CipherVault.Admin.v1", "broker": "CipherVault.Broker.v1", "bridge": "CipherVault.Bridge.v1", "rejectRemoteClients": true },
                      "allowedServerImages": [
                        {
                          "kind": "host",
                          "path": "{{imagePath}}",
                          "sha256": "{{Sha256}}",
                          "signerThumbprint": "{{Thumbprint}}"
                        }
                      ]
                    }
                    """);
            }
        }

        public string Root { get; }

        public string ManifestPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
