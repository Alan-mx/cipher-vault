using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class VaultModeStoreTests
{
    [Fact]
    public void StrictModeMarkerRoundTripsAtomically()
    {
        using var fixture = new ModeFixture();
        var installId = Guid.NewGuid();
        var manifest = new CipherVaultVaultModeManifest
        {
            Format = CipherVaultServiceConfiguration.VaultModeFormat,
            FormatVersion = CipherVaultServiceConfiguration.VaultModeFormatVersion,
            Mode = CipherVaultServiceConfiguration.StrictModeValue,
            OwnerSid = "S-1-5-21-1",
            InstallId = installId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        VaultModeStore.Write(fixture.ModePath, manifest);
        var loaded = VaultModeStore.Read(fixture.ModePath);

        Assert.Equal(installId, loaded.InstallId);
        Assert.Equal("strict", loaded.Mode);
        Assert.Empty(
            Directory.GetFiles(
                fixture.VaultPath,
                ".vault.mode.*.tmp",
                SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void CompatibilityModeRejectsAnyVaultMarker()
    {
        using var fixture = new ModeFixture();
        File.WriteAllText(fixture.ModePath, """{"format":"invalid"}""");

        var exception = Assert.Throws<VaultException>(
            () => VaultModeStore.EnsureCompatibilityAllowed(fixture.VaultPath));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void MissingModeMarkerIsNotReadableAsStrict()
    {
        using var fixture = new ModeFixture();

        var exception = Assert.Throws<VaultException>(
            () => VaultModeStore.Read(fixture.ModePath));

        Assert.Equal(VaultErrorCode.NotFound, exception.Code);
    }

    private sealed class ModeFixture : IDisposable
    {
        public ModeFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-mode-{Guid.NewGuid():N}");
            VaultPath = Path.Combine(Root, "Vault");
            Directory.CreateDirectory(VaultPath);
            ModePath = Path.Combine(VaultPath, "vault.mode");
        }

        public string Root { get; }

        public string VaultPath { get; }

        public string ModePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
