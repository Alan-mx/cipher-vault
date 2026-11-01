using System.Diagnostics;

namespace CipherVault.Phase8.ProcessProbe;

internal abstract class ServerProcess : IAsyncDisposable
{
    public abstract int ProcessId { get; }

    public abstract Task<int> WaitForExitAsync(TimeSpan timeout);

    public abstract Task RequestGracefulShutdownAsync();

    public abstract Task TerminateAsync();

    public abstract ValueTask DisposeAsync();
}

internal sealed class DetachedServerProcess(Process process)
    : ServerProcess
{
    public override int ProcessId => process.Id;

    public static DetachedServerProcess Start(
        string executable,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        var process = Process.Start(startInfo)
            ?? throw new ProbeException("server_start_failed");
        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);
        return new DetachedServerProcess(process);
    }

    public override async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync();
            throw new ProbeException("server_shutdown_timeout");
        }
        return process.ExitCode;
    }

    public override async Task RequestGracefulShutdownAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new ProbeException("unsupported_shutdown_platform");
        }
        using var signal = Process.Start(
            new ProcessStartInfo("/bin/kill")
            {
                UseShellExecute = false,
                ArgumentList =
                {
                    "-TERM",
                    process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            }) ?? throw new ProbeException("server_signal_failed");
        await signal.WaitForExitAsync();
        if (signal.ExitCode != 0)
        {
            throw new ProbeException("server_signal_failed");
        }
    }

    public override Task TerminateAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await TerminateAsync();
        process.Dispose();
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) > 0)
        {
        }
    }
}
