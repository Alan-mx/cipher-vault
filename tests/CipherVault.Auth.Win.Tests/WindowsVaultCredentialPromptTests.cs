using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CipherVault.Auth.Win;
using CipherVault.Core.Crypto;
using CipherVault.Core.Session;
using CipherVault.Security.Win;

namespace CipherVault.Auth.Win.Tests;

public sealed class WindowsVaultCredentialPromptTests
{
    [Fact]
    public async Task RecoveryKeyInputRoundTripsThroughPrompt()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        var rawKey = RandomNumberGenerator.GetBytes(32);
        var recoveryKey = RecoveryKeyCodec.Format(rawKey);
        var title = $"CipherVault Recovery Prompt Test {Guid.NewGuid():N}";
        var prompt = WindowsVaultCredentialPrompt.RequestAsync(
            title,
            "Enter the recovery key.",
            VaultCredentialType.RecoveryKey);
        var window = await WaitForWindowAsync(title);
        FocusWindow(window);
        SendText(window, recoveryKey);
        SendKey(window, 0x0D);

        var result = await prompt.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Confirmed);
        Assert.Equal(VaultCredentialType.RecoveryKey, result.CredentialType);
        Assert.NotNull(result.Credential);
        var parsed = RecoveryKeyCodec.Parse(result.Credential);
        Assert.True(CryptographicOperations.FixedTimeEquals(rawKey, parsed));
    }

    private static bool ShouldRunInteractiveUiTests()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return false;
        }
        if (new WindowsSystemActivityProvider().GetSnapshot().IsWorkstationLocked)
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

    private static void SendText(IntPtr window, string text)
    {
        foreach (var character in text)
        {
            _ = NativeMethods.PostMessage(
                window,
                0x0102,
                (IntPtr)character,
                IntPtr.Zero);
        }
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
                throw new TimeoutException(
                    "Timed out waiting for Windows UI automation lock.");
            }
            return new Releaser(semaphore);
        }

        private sealed class Releaser(Semaphore semaphore) : IDisposable
        {
            private Semaphore? _semaphore = semaphore;

            public void Dispose()
            {
                var value = Interlocked.Exchange(ref _semaphore, null);
                if (value is null)
                {
                    return;
                }
                value.Release();
                value.Dispose();
            }
        }
    }
}
