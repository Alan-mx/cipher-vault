using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class TransferCoordinatorTests
{
    [Fact]
    public void FreezeManifestCalculatesFileHashesAndCreatesManifest()
    {
        using var fixture = new TransferFixture();
        var workspace = fixture.Coordinator.CreateWorkspace(
            HostTransferDirection.Export,
            HostTransferPayloadKind.Directory,
            "S-1-5-1",
            logonSidValue: null,
            "payload");
        Directory.CreateDirectory(workspace.PayloadPath);
        var payloadPath = Path.Combine(workspace.PayloadPath, "note.md");
        var content = "staged transfer content";
        File.WriteAllText(payloadPath, content);

        var manifest = fixture.Coordinator.FreezeManifest(workspace);

        Assert.True(File.Exists(workspace.ManifestPath));
        Assert.Equal("cipher-vault-transfer", manifest.Format);
        Assert.Equal(workspace.TransferId, manifest.TransferId);
        var entry = Assert.Single(manifest.Files);
        Assert.Equal("payload/note.md", entry.RelativePath);
        Assert.Equal(content.Length, entry.SizeBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content))),
            entry.Sha256);
        using var document = JsonDocument.Parse(
            File.ReadAllText(workspace.ManifestPath));
        Assert.Equal(
            "cipher-vault-transfer",
            document.RootElement.GetProperty("Format").GetString());
    }

    [Fact]
    public void SweepExpiredRemovesExpiredWorkspace()
    {
        using var fixture = new TransferFixture(TimeSpan.Zero);
        var workspace = fixture.Coordinator.CreateWorkspace(
            HostTransferDirection.Export,
            HostTransferPayloadKind.File,
            "S-1-5-1",
            logonSidValue: null,
            "vault.cvbackup");

        Assert.Equal(1, fixture.Coordinator.SweepExpired());
        Assert.False(Directory.Exists(workspace.RootPath));
    }

    [Fact]
    public void CleanupOrphanedTransfersRemovesInactiveDirectories()
    {
        using var fixture = new TransferFixture();
        var orphan = Path.Combine(
            fixture.Paths.TransferRoot,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);

        Assert.Equal(1, fixture.Coordinator.CleanupOrphanedTransfers());
        Assert.False(Directory.Exists(orphan));
    }

    private sealed class TransferFixture : IDisposable
    {
        public TransferFixture()
            : this(TimeSpan.FromMinutes(30))
        {
        }

        public TransferFixture(TimeSpan lifetime)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-transfer-{Guid.NewGuid():N}");
            Paths = CipherVaultServicePaths.Create(
                Path.Combine(Root, "ProgramFiles"),
                Path.Combine(Root, "ProgramData"),
                Path.Combine(Root, "LocalAppData"));
            Directory.CreateDirectory(Paths.TransferRoot);
            Coordinator = new TransferCoordinator(Paths, lifetime);
        }

        public string Root { get; }
        public CipherVaultServicePaths Paths { get; }
        public TransferCoordinator Coordinator { get; }

        public void Dispose()
        {
            Coordinator.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
