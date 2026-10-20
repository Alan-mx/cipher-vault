using System.Runtime.InteropServices;
using CipherVault.Auth.Win;

namespace CipherVault.Auth.Win.Tests;

public sealed class WindowsPasswordPromptTests
{
    private const string Password = "prompt test 2026";

    [Fact]
    public async Task EmptyPasswordDoesNotCloseAndTypedPasswordIsReturned()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        var title = CreateTitle();
        var prompt = WindowsPasswordPrompt.RequestAsync(title, "Enter test password.");
        var window = await WaitForWindowAsync(title);
        FocusWindow(window);

        SendKey(window, 0x0D);
        await Task.Delay(200);
        Assert.NotEqual(IntPtr.Zero, NativeMethods.FindWindow(null, title));

        SubmitPassword(window, Password);
        var result = await prompt.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Confirmed);
        Assert.Equal(Password, result.Password);
    }

    [Fact]
    public async Task EscapeReturnsCancelledResult()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        var title = CreateTitle();
        var prompt = WindowsPasswordPrompt.RequestAsync(title, "Cancel this prompt.");
        var window = await WaitForWindowAsync(title);
        FocusWindow(window);

        SendKey(window, 0x1B);
        var result = await prompt.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Confirmed);
        Assert.Null(result.Password);
    }

    [Fact]
    public async Task CancellingTokenClosesOpenWindow()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        var title = CreateTitle();
        using var cancellation = new CancellationTokenSource();
        var prompt = WindowsPasswordPrompt.RequestAsync(
            title,
            "Cancel through the token.",
            cancellation.Token);
        _ = await WaitForWindowAsync(title);

        cancellation.Cancel();
        var result = await prompt.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Confirmed);
        Assert.Null(result.Password);
    }

    [Fact]
    public async Task PreCancelledTokenDoesNotOpenWindow()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var title = CreateTitle();

        var result = await WindowsPasswordPrompt.RequestAsync(
            title,
            "This prompt should not open.",
            cancellation.Token);

        Assert.False(result.Confirmed);
        Assert.Null(result.Password);
        Assert.Equal(IntPtr.Zero, NativeMethods.FindWindow(null, title));
    }

    private static string CreateTitle()
    {
        return $"CipherVault Prompt Test {Guid.NewGuid():N}";
    }

    private static bool ShouldRunInteractiveUiTests()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return false;
        }
        if (string.Equals(
                Environment.GetEnvironmentVariable("CIPHER_VAULT_RUN_UI_TESTS"),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return !string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IntPtr> WaitForWindowAsync(string title)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var window = NativeMethods.FindWindow(null, title);
            if (window != IntPtr.Zero)
            {
                return window;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Window '{title}' did not appear.");
    }

    private static void FocusWindow(IntPtr window)
    {
        _ = NativeMethods.ShowWindow(window, 5);
        _ = NativeMethods.SetForegroundWindow(window);
        Thread.Sleep(100);
    }

    private static void SubmitPassword(IntPtr window, string password)
    {
        foreach (var character in password)
        {
            _ = NativeMethods.PostMessage(
                window,
                0x0102,
                (IntPtr)character,
                IntPtr.Zero);
        }
        SendKey(window, 0x0D);
    }

    private static void SendKey(IntPtr window, int virtualKey)
    {
        _ = NativeMethods.PostMessage(
            window,
            0x0100,
            (IntPtr)virtualKey,
            IntPtr.Zero);
        _ = NativeMethods.PostMessage(
            window,
            0x0101,
            (IntPtr)virtualKey,
            IntPtr.Zero);
        Thread.Sleep(100);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string? className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);
    }

    private static class UiAutomationLock
    {
        private const string Name = @"Local\CipherVault.WindowsUiAutomationTests";

        public static IDisposable Acquire()
        {
            var semaphore = new Semaphore(1, 1, Name);
            if (!semaphore.WaitOne(TimeSpan.FromSeconds(30)))
            {
                semaphore.Dispose();
                throw new TimeoutException("Timed out waiting for Windows UI automation lock.");
            }
            return new Releaser(semaphore);
        }

        private sealed class Releaser : IDisposable
        {
            private Semaphore? _semaphore;

            public Releaser(Semaphore semaphore)
            {
                _semaphore = semaphore;
            }

            public void Dispose()
            {
                var semaphore = Interlocked.Exchange(ref _semaphore, null);
                if (semaphore is null)
                {
                    return;
                }
                semaphore.Release();
                semaphore.Dispose();
            }
        }
    }
}
