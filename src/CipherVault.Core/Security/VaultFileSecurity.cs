using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Security;

internal static class VaultFileSecurity
{
    public static void ProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectDirectoryWindows(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectDirectoryWindows(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddDirectoryRules(security);
            info.SetAccessControl(security);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to apply Vault directory permissions.",
                exception);
        }
    }

    public static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectFileWindows(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFileWindows(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddFileRules(security);
            info.SetAccessControl(security);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to apply Vault file permissions.",
                exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddDirectoryRules(DirectorySecurity security)
    {
        foreach (var identity in TrustedIdentities())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddFileRules(FileSecurity security)
    {
        foreach (var identity in TrustedIdentities())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<SecurityIdentifier> TrustedIdentities()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to resolve the current Windows user.");

        return
        [
            currentUser,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        ];
    }
}
