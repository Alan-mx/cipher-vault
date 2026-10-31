using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace CipherVault.Core.Tests;

public sealed class CoreBoundaryTests
{
    [Fact]
    public void ErrorContractHasNoRecoveryCredential()
    {
        Assert.DoesNotContain(
            "Recovery",
            string.Join(",", Enum.GetNames<VaultErrorCode>()),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(VaultErrorCode.UnsupportedVersion), Enum.GetNames<VaultErrorCode>());
    }

    [Fact]
    public void FilePermissionsCanBeApplied()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-permissions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "vault.key");
        File.WriteAllText(file, "test");

        try
        {
            VaultFilePermissions.ProtectDirectory(root);
            VaultFilePermissions.ProtectFile(file);

            Assert.True(Directory.Exists(root));
            Assert.True(File.Exists(file));

            if (OperatingSystem.IsWindows())
            {
                AssertWindowsPermissions(root, file);
            }
            else
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(root));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(file));
            }
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Phase1SourceHasNoLegacyCryptoDependencies()
    {
        var root = FindV2Root();
        var sourceRoot = Path.Combine(root, "src", "CipherVault.Core");
        var forbidden = new[]
        {
            "SQLCipher",
            "bundle_e_sqlcipher",
            "RecoveryKey",
            "RecoveryVerificationRequired",
            "recovery_slot",
            "ILogger",
            "Console.Write",
            "Serilog",
            "NLog"
        };
        var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            foreach (var value in forbidden)
            {
                Assert.DoesNotContain(value, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string FindV2Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CipherVault.V2.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("V2 solution root was not found.");
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsPermissions(string directoryPath, string filePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User;
        Assert.NotNull(currentUser);

        var directorySecurity = new DirectoryInfo(directoryPath).GetAccessControl();
        Assert.True(directorySecurity.AreAccessRulesProtected);
        var directoryRules = directorySecurity
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToArray();
        Assert.NotEmpty(directoryRules);
        Assert.All(
            directoryRules,
            rule => Assert.Equal(currentUser!.Value, rule.IdentityReference.Value));

        var fileSecurity = new FileInfo(filePath).GetAccessControl();
        Assert.True(fileSecurity.AreAccessRulesProtected);
        var fileRules = fileSecurity
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToArray();
        Assert.NotEmpty(fileRules);
        Assert.All(
            fileRules,
            rule => Assert.Equal(currentUser!.Value, rule.IdentityReference.Value));
    }
}
