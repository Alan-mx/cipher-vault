using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CipherVault.Ipc;

public static class PipeSecurityFactory
{
    public static PipeSecurity Create(
        IEnumerable<SecurityIdentifier> fullControlSids,
        IEnumerable<SecurityIdentifier>? readWriteSids = null,
        bool includeAdministrators = true,
        bool includeSystem = true)
    {
        ArgumentNullException.ThrowIfNull(fullControlSids);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var fullControl = Normalize(fullControlSids);
        foreach (var sid in fullControl.Values)
        {
            security.AddAccessRule(new PipeAccessRule(
                sid,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }
        if (readWriteSids is not null)
        {
            foreach (var sid in Normalize(readWriteSids).Values)
            {
                if (!fullControl.ContainsKey(sid.Value))
                {
                    security.AddAccessRule(new PipeAccessRule(
                        sid,
                        PipeAccessRights.ReadWrite,
                        AccessControlType.Allow));
                }
            }
        }
        if (includeAdministrators)
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }
        if (includeSystem)
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }
        return security;
    }

    private static Dictionary<string, SecurityIdentifier> Normalize(
        IEnumerable<SecurityIdentifier> sids)
    {
        return sids
            .Where(sid => sid is not null)
            .GroupBy(sid => sid.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
    }
}
