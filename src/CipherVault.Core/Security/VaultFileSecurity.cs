using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Security;

public sealed record VaultFileAccessRule(
    SecurityIdentifier Identity,
    FileSystemRights Rights,
    bool InheritToChildren = false);

public static class VaultFileSecurity
{
    public static void ProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectDirectoryWindows(path, TrustedIdentities());
        }
    }

    public static void ProtectDirectoryForSids(
        string path,
        IEnumerable<SecurityIdentifier> fullControlSids,
        bool includeAdministrators = true,
        bool includeSystem = true)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectDirectoryWindows(
                path,
                NormalizeIdentities(
                    fullControlSids,
                    includeAdministrators,
                    includeSystem));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectDirectoryWindows(
        string path,
        IReadOnlyCollection<SecurityIdentifier> identities)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddDirectoryRules(security, identities);
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
            ProtectFileWindows(path, TrustedIdentities());
        }
    }

    public static void ProtectFileForSids(
        string path,
        IEnumerable<SecurityIdentifier> fullControlSids,
        bool includeAdministrators = true,
        bool includeSystem = true)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectFileWindows(
                path,
                NormalizeIdentities(
                    fullControlSids,
                    includeAdministrators,
                    includeSystem));
        }
    }

    public static void ProtectDirectoryWithRules(
        string path,
        IEnumerable<VaultFileAccessRule> rules)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectDirectoryWithRulesWindows(path, NormalizeRules(rules));
        }
    }

    public static void ProtectFileWithRules(
        string path,
        IEnumerable<VaultFileAccessRule> rules)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectFileWithRulesWindows(path, NormalizeRules(rules));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFileWindows(
        string path,
        IReadOnlyCollection<SecurityIdentifier> identities)
    {
        try
        {
            var info = new FileInfo(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddFileRules(security, identities);
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
    private static void ProtectDirectoryWithRulesWindows(
        string path,
        IReadOnlyCollection<VaultFileAccessRule> rules)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(
                isProtected: true,
                preserveInheritance: false);
            foreach (var rule in rules)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    rule.Identity,
                    rule.Rights,
                    rule.InheritToChildren
                        ? InheritanceFlags.ContainerInherit |
                          InheritanceFlags.ObjectInherit
                        : InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to apply Vault directory permissions.",
                exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFileWithRulesWindows(
        string path,
        IReadOnlyCollection<VaultFileAccessRule> rules)
    {
        try
        {
            var info = new FileInfo(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(
                isProtected: true,
                preserveInheritance: false);
            foreach (var rule in rules)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    rule.Identity,
                    rule.Rights,
                    AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to apply Vault file permissions.",
                exception);
        }
    }

    public static void SetDirectoryOwner(string path, SecurityIdentifier owner)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.SetOwner(owner);
            info.SetAccessControl(security);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to set Vault directory owner.",
                exception);
        }
    }

    public static void SetFileOwner(string path, SecurityIdentifier owner)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetOwner(owner);
            info.SetAccessControl(security);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to set Vault file owner.",
                exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddDirectoryRules(
        DirectorySecurity security,
        IReadOnlyCollection<SecurityIdentifier> identities)
    {
        foreach (var identity in identities)
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
    private static void AddFileRules(
        FileSecurity security,
        IReadOnlyCollection<SecurityIdentifier> identities)
    {
        foreach (var identity in identities)
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

    [SupportedOSPlatform("windows")]
    private static IReadOnlyCollection<SecurityIdentifier> NormalizeIdentities(
        IEnumerable<SecurityIdentifier> fullControlSids,
        bool includeAdministrators,
        bool includeSystem)
    {
        ArgumentNullException.ThrowIfNull(fullControlSids);
        var identities = fullControlSids
            .Where(identity => identity is not null)
            .GroupBy(identity => identity.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        if (includeSystem)
        {
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            identities[systemSid.Value] = systemSid;
        }
        if (includeAdministrators)
        {
            var administratorsSid = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            identities[administratorsSid.Value] = administratorsSid;
        }
        if (identities.Count == 0)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "At least one access identity is required.");
        }
        return identities.Values.ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyCollection<VaultFileAccessRule> NormalizeRules(
        IEnumerable<VaultFileAccessRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var normalized = rules
            .Select(rule =>
            {
                ArgumentNullException.ThrowIfNull(rule);
                ArgumentNullException.ThrowIfNull(rule.Identity);
                if (rule.Rights == 0)
                {
                    throw new VaultException(
                        VaultErrorCode.InvalidInput,
                        "Vault file access rights cannot be empty.");
                }
                return rule;
            })
            .GroupBy(
                rule => (
                    rule.Identity.Value,
                    rule.Rights,
                    rule.InheritToChildren))
            .Select(group => group.First())
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "At least one access rule is required.");
        }
        return normalized;
    }
}
