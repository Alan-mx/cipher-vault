using CipherVault.Core.Backup;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CipherVault.Server.Hosting;

public interface IInteractiveConsole
{
    bool IsInteractive { get; }

    Task<string?> ReadPasswordAsync(
        CancellationToken cancellationToken);

    void WriteSafeError(RestoreOutcome outcome);

    void WriteSafeSuccess(RestoreVerificationResult result);
}

internal sealed class InteractiveConsoleTerminal : IInteractiveConsole
{
    public bool IsInteractive =>
        OperatingSystem.IsWindows()
            ? WindowsConsole.HasInputAndOutput()
            : !Console.IsInputRedirected
                && !Console.IsOutputRedirected;

    public Task<string?> ReadPasswordAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.Error.Write("Backup password: ");
        var characters = new List<char>();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.Error.WriteLine();
                    return Task.FromResult<string?>(
                        new string(characters.ToArray()));
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (characters.Count > 0)
                    {
                        characters.RemoveAt(characters.Count - 1);
                    }
                    continue;
                }
                if (key.Key == ConsoleKey.C
                    && (key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    throw new OperationCanceledException(
                        cancellationToken);
                }
                if (!char.IsControl(key.KeyChar))
                {
                    characters.Add(key.KeyChar);
                }
            }
        }
        finally
        {
            characters.Clear();
        }
    }

    public void WriteSafeError(RestoreOutcome outcome)
    {
        Console.Error.WriteLine(
            $"{outcome.FailureCode}: {outcome.SafeMessage}");
    }

    public void WriteSafeSuccess(RestoreVerificationResult result)
    {
        Console.Out.WriteLine(
            $"restore_completed formatVersion={result.FormatVersion} schemaVersion={result.SchemaVersion} databaseBytes={result.DatabaseBytes} keyEnvelopeBytes={result.KeyEnvelopeBytes}");
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsConsole
    {
        private const int StandardInputHandle = -10;
        private const int StandardOutputHandle = -11;

        public static bool HasInputAndOutput()
        {
            var input = GetStdHandle(StandardInputHandle);
            var output = GetStdHandle(StandardOutputHandle);
            return input != IntPtr.Zero
                && input != new IntPtr(-1)
                && output != IntPtr.Zero
                && output != new IntPtr(-1)
                && GetConsoleMode(input, out _)
                && GetConsoleMode(output, out _);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(
            IntPtr consoleHandle,
            out uint mode);
    }
}
