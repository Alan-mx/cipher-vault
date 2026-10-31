using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Security;

public static class VaultFilePermissions
{
    public static void ProtectDirectory(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ProtectDirectoryWindows(path);
                return;
            }

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to protect the Vault directory.");
        }
    }

    public static void ProtectFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ProtectFileWindows(path);
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to protect the Vault file.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectDirectoryWindows(string path)
    {
        var info = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUserSid(),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFileWindows(string path)
    {
        var info = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUserSid(),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User
            ?? throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to determine the current Windows user.");
        return new SecurityIdentifier(sid.Value);
    }
}
