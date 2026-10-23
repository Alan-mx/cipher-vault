using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
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
    public void VerifyManifestAcceptsFrozenWorkspace()
    {
        using var fixture = new TransferFixture();
        using var workspace = CreateDirectoryWorkspace(
            fixture,
            "note.md",
            "staged transfer content");
        var frozen = fixture.Coordinator.FreezeManifest(workspace);

        var verified = fixture.Coordinator.VerifyManifest(workspace);

        Assert.Equal(frozen.TransferId, verified.TransferId);
        Assert.Equal(frozen.ExpiresAt, verified.ExpiresAt);
        Assert.Single(verified.Files);
    }

    [Fact]
    public void FreezeManifestGrantsOwnerReadAccessToProtectedExportDirectory()
    {
        const string ownerSid = "S-1-5-21-900";
        const string logonSid = "S-1-5-5-1-900";
        using var fixture = new TransferFixture();
        using var workspace = fixture.Coordinator.CreateWorkspace(
            HostTransferDirection.Export,
            HostTransferPayloadKind.Directory,
            ownerSid,
            logonSid,
            "payload");
        var nested = Path.Combine(workspace.PayloadPath, "nested");
        var note = Path.Combine(nested, "note.md");
        Directory.CreateDirectory(nested);
        File.WriteAllText(note, "export content");
        VaultFileSecurity.ProtectDirectory(nested);
        VaultFileSecurity.ProtectFile(note);
        VaultFileSecurity.ProtectDirectory(workspace.PayloadPath);

        fixture.Coordinator.FreezeManifest(workspace);

        AssertDirectoryReadAccess(workspace.PayloadPath, ownerSid, logonSid);
        AssertDirectoryReadAccess(nested, ownerSid, logonSid);
        AssertFileReadAccess(note, ownerSid, logonSid);
    }

    [Fact]
    public void FreezeManifestGrantsOwnerReadAccessToProtectedExportFile()
    {
        const string ownerSid = "S-1-5-21-901";
        const string logonSid = "S-1-5-5-1-901";
        using var fixture = new TransferFixture();
        using var workspace = fixture.Coordinator.CreateWorkspace(
            HostTransferDirection.Export,
            HostTransferPayloadKind.File,
            ownerSid,
            logonSid,
            "vault.cvbackup");
        File.WriteAllText(workspace.PayloadPath, "backup payload");
        VaultFileSecurity.ProtectFile(workspace.PayloadPath);

        fixture.Coordinator.FreezeManifest(workspace);

        AssertFileReadAccess(workspace.PayloadPath, ownerSid, logonSid);
    }

    [Fact]
    public void VerifyManifestRejectsTamperedFile()
    {
        using var fixture = new TransferFixture();
        using var workspace = CreateDirectoryWorkspace(
            fixture,
            "note.md",
            "original content");
        fixture.Coordinator.FreezeManifest(workspace);
        File.WriteAllText(
            Path.Combine(workspace.PayloadPath, "note.md"),
            "tampered content");

        var exception = Assert.Throws<VaultException>(() =>
            fixture.Coordinator.VerifyManifest(workspace));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void VerifyManifestRejectsUnlistedFiles()
    {
        using var fixture = new TransferFixture();
        using var workspace = CreateDirectoryWorkspace(
            fixture,
            "note.md",
            "original content");
        fixture.Coordinator.FreezeManifest(workspace);
        File.WriteAllText(
            Path.Combine(workspace.RootPath, "extra.txt"),
            "extra");

        var exception = Assert.Throws<VaultException>(() =>
            fixture.Coordinator.VerifyManifest(workspace));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void VerifyManifestRejectsMissingFiles()
    {
        using var fixture = new TransferFixture();
        using var workspace = CreateDirectoryWorkspace(
            fixture,
            "note.md",
            "original content");
        fixture.Coordinator.FreezeManifest(workspace);
        File.Delete(Path.Combine(workspace.PayloadPath, "note.md"));

        var exception = Assert.Throws<VaultException>(() =>
            fixture.Coordinator.VerifyManifest(workspace));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void VerifyManifestRejectsPathsOutsideStaging()
    {
        using var fixture = new TransferFixture();
        using var workspace = CreateDirectoryWorkspace(
            fixture,
            "note.md",
            "original content");
        var manifest = new TransferManifest(
            TransferCoordinator.ManifestFormat,
            TransferCoordinator.ManifestFormatVersion,
            workspace.TransferId,
            workspace.Direction,
            workspace.PayloadKind,
            [
                new TransferFileEntry(
                    "../outside.txt",
                    1,
                    new string('A', 64))
            ],
            DateTimeOffset.UtcNow,
            workspace.ExpiresAt);
        WriteManifest(workspace, manifest);

        var exception = Assert.Throws<VaultException>(() =>
            fixture.Coordinator.VerifyManifest(workspace));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void VerifyManifestRejectsReparsePoints()
    {
        using var fixture = new TransferFixture();
        using var workspace = CreateDirectoryWorkspace(
            fixture,
            "note.md",
            "original content");
        fixture.Coordinator.FreezeManifest(workspace);
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        CreateJunction(
            Path.Combine(workspace.RootPath, "link"),
            outside);

        var exception = Assert.Throws<VaultException>(() =>
            fixture.Coordinator.VerifyManifest(workspace));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
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

    private static TransferWorkspace CreateDirectoryWorkspace(
        TransferFixture fixture,
        string fileName,
        string content)
    {
        var workspace = fixture.Coordinator.CreateWorkspace(
            HostTransferDirection.Export,
            HostTransferPayloadKind.Directory,
            "S-1-5-1",
            logonSidValue: null,
            "payload");
        Directory.CreateDirectory(workspace.PayloadPath);
        File.WriteAllText(
            Path.Combine(workspace.PayloadPath, fileName),
            content);
        return workspace;
    }

    private static void AssertDirectoryReadAccess(
        string path,
        params string[] expectedSids)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        foreach (var sid in expectedSids)
        {
            var identifier = new SecurityIdentifier(sid);
            Assert.Contains(rules, rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(identifier) &&
                (rule.FileSystemRights & FileSystemRights.ReadAndExecute) ==
                FileSystemRights.ReadAndExecute);
        }
    }

    private static void AssertFileReadAccess(
        string path,
        params string[] expectedSids)
    {
        var security = new FileInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        foreach (var sid in expectedSids)
        {
            var identifier = new SecurityIdentifier(sid);
            Assert.Contains(rules, rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(identifier) &&
                (rule.FileSystemRights & FileSystemRights.ReadAndExecute) ==
                FileSystemRights.ReadAndExecute);
        }
    }

    private static void WriteManifest(
        TransferWorkspace workspace,
        TransferManifest manifest)
    {
        File.WriteAllText(
            workspace.ManifestPath,
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions
                {
                    Converters =
                    {
                        new JsonStringEnumConverter(
                            JsonNamingPolicy.CamelCase)
                    }
                }));
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cmd.exe could not be started.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Junction creation failed: {standardOutput}{standardError}");
        }
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
