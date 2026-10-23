using System.Runtime.InteropServices;

namespace CipherVault.Auth.Win;

internal sealed class WindowsDialogCloseScope : IDisposable
{
    private const uint WindowCloseMessage = 0x0010;
    private const int WindowTextCapacity = 512;
    private readonly string _title;
    private readonly CancellationTokenRegistration _registration;
    private readonly Timer _timer;
    private int _closeRequested;
    private int _closePosted;

    public WindowsDialogCloseScope(
        string title,
        CancellationToken cancellationToken)
    {
        _title = title;
        _registration = cancellationToken.Register(
            static state => ((WindowsDialogCloseScope)state!).RequestClose(),
            this);
        _timer = new Timer(
            static state => ((WindowsDialogCloseScope)state!).TryPostClose(),
            this,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100));
    }

    public void Dispose()
    {
        _timer.Dispose();
        _registration.Dispose();
    }

    private void RequestClose()
    {
        Volatile.Write(ref _closeRequested, 1);
        TryPostClose();
    }

    private void TryPostClose()
    {
        if (Volatile.Read(ref _closeRequested) == 0)
        {
            return;
        }
        var window = FindDialogWindow();
        if (window == IntPtr.Zero ||
            Interlocked.Exchange(ref _closePosted, 1) != 0)
        {
            return;
        }
        _ = PostMessage(
            window,
            WindowCloseMessage,
            IntPtr.Zero,
            IntPtr.Zero);
    }

    private IntPtr FindDialogWindow()
    {
        var result = IntPtr.Zero;
        _ = EnumWindows(
            (window, parameter) =>
            {
                _ = GetWindowThreadProcessId(window, out var processId);
                if (processId != Environment.ProcessId)
                {
                    return true;
                }
                var text = new char[WindowTextCapacity];
                var length = GetWindowText(window, text, text.Length);
                if (length <= 0 ||
                    !string.Equals(
                        new string(text, 0, length),
                        _title,
                        StringComparison.Ordinal))
                {
                    return true;
                }
                result = window;
                return false;
            },
            IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsCallback(
        IntPtr window,
        IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr window,
        [Out] char[] text,
        int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
