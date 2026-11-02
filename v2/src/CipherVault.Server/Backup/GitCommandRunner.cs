using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CipherVault.Server.Backup;

internal sealed record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

internal interface IGitCommandRunner
{
    Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class GitCommandRunner : IGitCommandRunner
{
    private const int MaxCapturedCharacters = 64 * 1024;

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        var controlledEnvironment = environment
            ?? GitExecutionEnvironment.CreateBaseEnvironment();
        foreach (var pair in controlledEnvironment)
        {
            if (GitExecutionEnvironment.IsAllowedVariable(pair.Key))
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new GitCommandException(
                    GitBackupErrorCodes.GitUnavailable);
            }
        }
        catch (Win32Exception exception)
        {
            throw new GitCommandException(
                GitBackupErrorCodes.GitUnavailable,
                exception);
        }

        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput,
            cancellationToken);
        var stderrTask = ReadBoundedAsync(
            process.StandardError,
            cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = await stdoutTask;
        var error = await stderrTask;
        return new GitCommandResult(
            timedOut ? -1 : process.ExitCode,
            output,
            error,
            timedOut);
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var captured = new StringBuilder(
            capacity: Math.Min(MaxCapturedCharacters, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            var remaining = MaxCapturedCharacters - captured.Length;
            if (remaining > 0)
            {
                captured.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return captured.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or NotSupportedException)
        {
            // Preserve timeout or cancellation as the stable operation result.
        }
    }
}

internal sealed class GitCommandException : Exception
{
    public GitCommandException(
        string code,
        Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
