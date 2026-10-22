using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CipherVault.Core.Errors;
using Microsoft.Win32.SafeHandles;

namespace CipherVault.Core.Security;

internal static class VaultPathIdentity
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public static string GetLockIdentity(
        string vaultPath,
        Func<string, string?>? resolveExistingPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        var resolved = ResolveFinalPath(vaultPath, resolveExistingPath);
        return resolved.ToUpperInvariant();
    }

    internal static string ResolveFinalPath(
        string path,
        Func<string, string?>? resolveExistingPath = null)
    {
        var fullPath = NormalizePath(Path.GetFullPath(path));
        var pendingSegments = new Stack<string>();
        var current = fullPath;

        while (!Directory.Exists(current) && !File.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Vault path identity could not be resolved.");
            }

            pendingSegments.Push(Path.GetFileName(current));
            current = parent;
        }

        var resolved = resolveExistingPath is null
            ? ResolveExistingPathWindows(current)
            : resolveExistingPath(current) ?? throw new VaultException(
                VaultErrorCode.StorageError,
                "Vault path identity could not be resolved.");

        while (pendingSegments.Count > 0)
        {
            resolved = Path.Combine(resolved, pendingSegments.Pop());
        }

        return NormalizePath(Path.GetFullPath(resolved));
    }

    private static string ResolveExistingPathWindows(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        using var handle = CreateFile(
            path,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Vault path identity could not be resolved.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var capacity = 512u;
        while (true)
        {
            var buffer = new StringBuilder((int)capacity);
            var length = GetFinalPathNameByHandle(
                handle,
                buffer,
                capacity,
                flags: 0);
            if (length == 0)
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Vault path identity could not be resolved.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
            if (length < capacity)
            {
                return NormalizeFinalPath(buffer.ToString());
            }

            capacity = length + 1;
        }
    }

    private static string NormalizeFinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        const string extendedPrefix = @"\\?\";
        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static string NormalizePath(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && path.Length > root.Length
            ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
    }

    [SupportedOSPlatform("windows")]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [SupportedOSPlatform("windows")]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);
}
