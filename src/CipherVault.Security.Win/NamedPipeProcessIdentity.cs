using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CipherVault.Security.Win;

public static class NamedPipeProcessIdentity
{
    public static uint GetServerProcessId(SafePipeHandle pipeHandle)
    {
        if (!NativeMethods.GetNamedPipeServerProcessId(
                pipeHandle,
                out var processId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return processId;
    }

    public static uint GetClientProcessId(SafePipeHandle pipeHandle)
    {
        if (!NativeMethods.GetNamedPipeClientProcessId(
                pipeHandle,
                out var processId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return processId;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeServerProcessId(
            SafePipeHandle pipe,
            out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeClientProcessId(
            SafePipeHandle pipe,
            out uint clientProcessId);
    }
}
