using System.Runtime.InteropServices;
using PersonalVault.Core.Session;

namespace PersonalVault.Auth.Win;

public sealed class WindowsSystemActivityProvider : ISystemActivityProvider
{
    private const uint DesktopReadObjects = 0x0001;
    private const int WtsCurrentSession = -1;
    private const int WtsSessionInfoEx = 25;

    public SystemActivitySnapshot GetSnapshot()
    {
        var input = new LastInputInfo
        {
            cbSize = (uint)Marshal.SizeOf<LastInputInfo>()
        };
        if (!GetLastInputInfo(ref input))
        {
            return new SystemActivitySnapshot(TimeSpan.FromDays(1), true);
        }

        var elapsedTicks = unchecked(GetTickCount() - input.dwTime);
        var idleTime = TimeSpan.FromMilliseconds(elapsedTicks);
        return new SystemActivitySnapshot(idleTime, IsWorkstationLocked());
    }

    private static bool IsWorkstationLocked()
    {
        if (TryGetSessionLockState(out var locked))
        {
            return locked;
        }
        return !CanOpenInputDesktop();
    }

    private static bool TryGetSessionLockState(out bool locked)
    {
        locked = false;
        if (!WTSQuerySessionInformation(
                IntPtr.Zero,
                WtsCurrentSession,
                WtsSessionInfoEx,
                out var buffer,
                out var bytesReturned))
        {
            return false;
        }

        try
        {
            if (bytesReturned < 20)
            {
                return false;
            }

            // WTSINFOEXW: Level at 0, 8-byte aligned WTSINFOEX_LEVEL1, then
            // SessionId (8), SessionState (12), SessionFlags (16).
            var sessionFlags = Marshal.ReadInt32(buffer, 16);
            if (sessionFlags is 0 or 1)
            {
                locked = sessionFlags == 0;
                return true;
            }
            return false;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static bool CanOpenInputDesktop()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        _ = CloseDesktop(desktop);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        int sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
