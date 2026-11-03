using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CipherVault.Core.Backup;

internal sealed record PlatformFileIdentity(
    PlatformIdentityKind Kind,
    ulong VolumeId,
    ulong FileIdLow,
    ulong FileIdHigh,
    long ChangeTimeSeconds,
    long ChangeTimeNanoseconds);

internal static class PlatformFileIdentityReader
{
    private static readonly PlatformFileIdentity Unreliable = new(
        PlatformIdentityKind.Unreliable,
        0,
        0,
        0,
        0,
        0);

    internal static PlatformFileIdentity Read(SafeFileHandle handle)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindows(handle);
            }

            if (OperatingSystem.IsLinux())
            {
                return ReadLinux(handle);
            }

            return Unreliable;
        }
        catch
        {
            return Unreliable;
        }
    }

    private static PlatformFileIdentity ReadWindows(SafeFileHandle handle)
    {
        if (!GetFileIdInfoNative(
                handle,
                18,
                out var fileId,
                (uint)Marshal.SizeOf<FileIdInfo>())
            || !GetFileBasicInfoNative(
                handle,
                0,
                out var basic,
                (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            return Unreliable;
        }

        if (fileId.FileId.Low == 0 && fileId.FileId.High == 0)
        {
            return Unreliable;
        }

        return new PlatformFileIdentity(
            PlatformIdentityKind.WindowsFileId128,
            fileId.VolumeSerialNumber,
            fileId.FileId.Low,
            fileId.FileId.High,
            basic.ChangeTime,
            0);
    }

    private static PlatformFileIdentity ReadLinux(SafeFileHandle handle)
    {
        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (statx(
                descriptor,
                string.Empty,
                AtEmptyPath,
                StatxBasicStats,
                out var value) != 0
            || (value.Mask & RequiredStatxMask) != RequiredStatxMask
            || value.Inode == 0)
        {
            return Unreliable;
        }

        var volume = ((ulong)value.DeviceMajor << 32)
            | value.DeviceMinor;
        return new PlatformFileIdentity(
            PlatformIdentityKind.LinuxStatx,
            volume,
            value.Inode,
            0,
            value.ChangeTime.Seconds,
            value.ChangeTime.Nanoseconds);
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInfoNative(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileBasicInfoNative(
        SafeFileHandle file,
        int fileInformationClass,
        out FileBasicInfo fileInformation,
        uint bufferSize);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out Statx buffer);

    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x07ff;
    private const uint RequiredStatxMask = 0x0381;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        internal long Seconds;
        internal uint Nanoseconds;
        private readonly int reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Statx
    {
        internal uint Mask;
        internal uint BlockSize;
        internal ulong Attributes;
        internal uint HardLinkCount;
        internal uint UserId;
        internal uint GroupId;
        internal ushort Mode;
        private readonly ushort spare0;
        internal ulong Inode;
        internal ulong Size;
        internal ulong Blocks;
        internal ulong AttributesMask;
        internal StatxTimestamp AccessTime;
        internal StatxTimestamp BirthTime;
        internal StatxTimestamp ChangeTime;
        internal StatxTimestamp ModificationTime;
        internal uint DeviceMajor;
        internal uint DeviceMinor;
        internal uint DeviceMajorHigh;
        internal uint DeviceMinorHigh;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 14)]
        private readonly ulong[] spare2;
    }
}
