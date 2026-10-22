using System.Runtime.InteropServices;
using CipherVault.Core.Session;

namespace CipherVault.Security.Win;

public sealed class WindowsSystemActivityProvider : ISystemActivityProvider
{
    private const uint DesktopReadObjects = 0x0001;
    private const int WtsCurrentSession = -1;
    private const int WtsSessionInfoEx = 25;
    private const uint NoConsoleSession = 0xFFFFFFFF;

    public SystemActivitySnapshot GetSnapshot()
    {
        return GetSnapshot(
            unchecked((uint)WtsCurrentSession));
    }

    public static SystemActivitySnapshot GetSnapshot(uint sessionId)
    {
        var idleTime = GetIdleTime();
        return new SystemActivitySnapshot(
            idleTime,
            IsWorkstationLocked(sessionId));
    }

    public static uint GetConsoleSessionId()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        return sessionId == NoConsoleSession ? 0 : sessionId;
    }

    private static TimeSpan GetIdleTime()
    {
        var input = new LastInputInfo
        {
            cbSize = (uint)Marshal.SizeOf<LastInputInfo>()
        };
        if (!GetLastInputInfo(ref input))
        {
            return TimeSpan.FromDays(1);
        }

        var elapsedTicks = unchecked(GetTickCount() - input.dwTime);
        return TimeSpan.FromMilliseconds(elapsedTicks);
    }

    private static bool IsWorkstationLocked(uint sessionId)
    {
        if (TryGetSessionLockState(sessionId, out var locked))
        {
            return locked;
        }

        return sessionId != unchecked((uint)WtsCurrentSession) ||
            !CanOpenInputDesktop();
    }

    private static bool TryGetSessionLockState(
        uint sessionId,
        out bool locked)
    {
        locked = false;
        if (!WTSQuerySessionInformation(
                IntPtr.Zero,
                unchecked((int)sessionId),
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

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
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
