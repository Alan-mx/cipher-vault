using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CipherVault.Security.Win;

public static class WindowsProcessAccessGrant
{
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int TokenQuery = 0x0008;
    private const uint TokenRead = 0x00020008;
    private const uint TokenWriteDac = 0x00040000;
    private const int DaclSecurityInformation = 0x00000004;
    private const int OwnerGroupDaclSecurityInformation = 0x00000007;

    public static void GrantVerificationAccessToCurrentProcess(string targetSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSid);
        var identifier = new SecurityIdentifier(targetSid);
        GrantProcessAccess(identifier);
        GrantTokenAccess(identifier);
    }

    private static void GrantProcessAccess(SecurityIdentifier identifier)
    {
        AddAccess(
            NativeMethods.GetCurrentProcess(),
            identifier,
            ProcessQueryLimitedInformation);
    }

    private static void GrantTokenAccess(SecurityIdentifier identifier)
    {
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                TokenRead | TokenWriteDac,
                out var token))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to open the current process token for DACL update.");
        }
        using (token)
        {
            AddAccess(token.DangerousGetHandle(), identifier, TokenQuery);
        }
    }

    private static void AddAccess(
        IntPtr handle,
        SecurityIdentifier identifier,
        int accessMask)
    {
        var current = ReadSecurityDescriptor(handle);
        var descriptor = new CommonSecurityDescriptor(
            isContainer: false,
            isDS: false,
            current,
            offset: 0);
        var dacl = descriptor.DiscretionaryAcl
            ?? new DiscretionaryAcl(
                isContainer: false,
                isDS: false,
                capacity: 0);
        foreach (GenericAce ace in dacl)
        {
            if (ace is CommonAce common &&
                common.AceQualifier == AceQualifier.AccessAllowed &&
                common.SecurityIdentifier.Equals(identifier) &&
                (common.AccessMask & accessMask) == accessMask)
            {
                return;
            }
        }
        dacl.AddAccess(
            AccessControlType.Allow,
            identifier,
            accessMask,
            InheritanceFlags.None,
            PropagationFlags.None);
        descriptor.DiscretionaryAcl = dacl;
        var updated = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(updated, offset: 0);
        var buffer = Marshal.AllocHGlobal(updated.Length);
        try
        {
            Marshal.Copy(updated, 0, buffer, updated.Length);
            if (!NativeMethods.SetKernelObjectSecurity(
                    handle,
                    DaclSecurityInformation,
                    buffer))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to update the kernel object DACL.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] ReadSecurityDescriptor(IntPtr handle)
    {
        _ = NativeMethods.GetKernelObjectSecurity(
            handle,
            OwnerGroupDaclSecurityInformation,
            IntPtr.Zero,
            0,
            out var required);
        if (required == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to read the kernel object security descriptor.");
        }
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!NativeMethods.GetKernelObjectSecurity(
                    handle,
                    OwnerGroupDaclSecurityInformation,
                    buffer,
                    required,
                    out required))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to read the kernel object security descriptor.");
            }
            var descriptor = new byte[required];
            Marshal.Copy(buffer, descriptor, 0, (int)required);
            return descriptor;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out SafeAccessTokenHandle tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetKernelObjectSecurity(
            IntPtr handle,
            int securityInformation,
            IntPtr securityDescriptor,
            uint length,
            out uint required);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetKernelObjectSecurity(
            IntPtr handle,
            int securityInformation,
            IntPtr securityDescriptor);
    }
}
