using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CipherVault.Ipc;

public static class NamedPipeServerFactory
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint DefaultBufferBytes = 64 * 1024;

    public static NamedPipeServerStream Create(
        string pipeName,
        PipeSecurity security,
        int maxInstances = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(security);
        if (maxInstances < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInstances));
        }

        var normalizedName = NormalizePipeName(pipeName);
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptorPointer,
                InheritHandle = false
            };
            var handle = NativeMethods.CreateNamedPipe(
                @"\\.\pipe\" + normalizedName,
                PipeAccessDuplex |
                FileFlagFirstPipeInstance |
                FileFlagOverlapped,
                PipeRejectRemoteClients,
                (uint)maxInstances,
                DefaultBufferBytes,
                DefaultBufferBytes,
                0,
                ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(
                    $"Named pipe creation failed with Win32 error {error}.",
                    new Win32Exception(error));
            }

            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorPointer);
        }
    }

    public static string NormalizePipeName(string pipeName)
    {
        const string prefix = @"\\.\pipe\";
        var normalized = pipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? pipeName[prefix.Length..]
            : pipeName;
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Length > 128 ||
            normalized.Contains('\\') ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '_' or '-')))
        {
            throw new ArgumentException("Named pipe name is invalid.", nameof(pipeName));
        }
        return normalized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateNamedPipeW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern SafePipeHandle CreateNamedPipe(
            string name,
            uint openMode,
            uint pipeMode,
            uint maxInstances,
            uint outBufferSize,
            uint inBufferSize,
            uint defaultTimeout,
            ref SecurityAttributes securityAttributes);
    }
}
