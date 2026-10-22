using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CipherVault.Core.Security;

namespace CipherVault.Core.Tests;

public sealed class VaultFileSecurityTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void ExplicitSidsCanProtectDirectoryAndFileWithoutImplicitUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SecurityFixture();
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var directory = Path.Combine(fixture.Root, "data");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "service.json");
        File.WriteAllText(file, "{}");

        VaultFileSecurity.ProtectDirectoryForSids(
            directory,
            [currentUser, currentUser],
            includeAdministrators: false,
            includeSystem: false);
        VaultFileSecurity.ProtectFileForSids(
            file,
            [currentUser],
            includeAdministrators: false,
            includeSystem: false);

        var directorySecurity = new DirectoryInfo(directory).GetAccessControl();
        var fileSecurity = new FileInfo(file).GetAccessControl();
        Assert.True(directorySecurity.AreAccessRulesProtected);
        Assert.True(fileSecurity.AreAccessRulesProtected);
        Assert.Single(
            directorySecurity.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                targetType: typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>());
        Assert.Single(
            fileSecurity.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                targetType: typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void OwnerCanBeSetExplicitly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SecurityFixture();
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var directory = Path.Combine(fixture.Root, "owned");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "vault.mode");
        File.WriteAllText(file, "{}");

        VaultFileSecurity.SetDirectoryOwner(directory, currentUser);
        VaultFileSecurity.SetFileOwner(file, currentUser);

        Assert.Equal(
            currentUser.Value,
            new DirectoryInfo(directory).GetAccessControl()
                .GetOwner(typeof(SecurityIdentifier))!
                .Value);
        Assert.Equal(
            currentUser.Value,
            new FileInfo(file).GetAccessControl()
                .GetOwner(typeof(SecurityIdentifier))!
                .Value);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ExplicitRulesCanGrantReadOnlyOwnerAndFullControlService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SecurityFixture();
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            null);
        var directory = Path.Combine(fixture.Root, "protected");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "install-public.json");
        File.WriteAllText(file, "{}");

        try
        {
            VaultFileSecurity.ProtectDirectoryWithRules(
                directory,
                [
                    new VaultFileAccessRule(
                        currentUser,
                        FileSystemRights.ReadAndExecute),
                    new VaultFileAccessRule(
                        system,
                        FileSystemRights.FullControl,
                        InheritToChildren: true)
                ]);
            VaultFileSecurity.ProtectFileWithRules(
                file,
                [
                    new VaultFileAccessRule(currentUser, FileSystemRights.Read),
                    new VaultFileAccessRule(system, FileSystemRights.FullControl)
                ]);

            var directoryRules = ReadRules(
                new DirectoryInfo(directory).GetAccessControl());
            var fileRules = ReadRules(new FileInfo(file).GetAccessControl());
            Assert.Equal(
                FileSystemRights.ReadAndExecute,
                directoryRules[currentUser.Value]);
            Assert.Equal(
                FileSystemRights.FullControl & ~FileSystemRights.Synchronize,
                directoryRules[system.Value]);
            Assert.Equal(FileSystemRights.Read, fileRules[currentUser.Value]);
            Assert.Equal(
                FileSystemRights.FullControl & ~FileSystemRights.Synchronize,
                fileRules[system.Value]);
        }
        finally
        {
            VaultFileSecurity.ProtectDirectory(directory);
            VaultFileSecurity.ProtectFile(file);
        }
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, FileSystemRights> ReadRules(
        FileSystemSecurity security)
    {
        Assert.True(security.AreAccessRulesProtected);
        return security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .GroupBy(rule => rule.IdentityReference.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(
                    (FileSystemRights)0,
                    (rights, rule) => rights | rule.FileSystemRights) &
                    ~FileSystemRights.Synchronize,
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SecurityFixture : IDisposable
    {
        public SecurityFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-security-{Guid.NewGuid():N}");
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
