using System.Diagnostics;
using System.IO;
using CipherVault.Core.Errors;

namespace CipherVault.Host.ServiceManagement;

public sealed record WindowsServiceCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IWindowsServiceCommandRunner
{
    Task<WindowsServiceCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class ScExeCommandRunner : IWindowsServiceCommandRunner
{
    public async Task<WindowsServiceCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new ArgumentException(
                "At least one sc.exe argument is required.",
                nameof(arguments));
        }

        var startInfo = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Unable to start sc.exe.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Windows service command timed out.");
            }
            return new WindowsServiceCommandResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Windows service command could not be executed.",
                exception);
        }
    }
}

public sealed class WindowsServiceOperations(
    IWindowsServiceQuery query,
    IWindowsServiceCommandRunner commandRunner)
{
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceDoesNotExist = 1060;

    public async Task<WindowsServiceSnapshot> StartAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        var current = query.GetStatus(serviceName);
        if (!current.Installed)
        {
            throw NotFound(serviceName);
        }
        if (string.Equals(current.State, "running", StringComparison.Ordinal))
        {
            return current;
        }

        var result = await commandRunner.RunAsync(
            ["start", serviceName],
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, serviceName, ErrorServiceAlreadyRunning);
        var updated = query.GetStatus(serviceName);
        return string.Equals(updated.State, "running", StringComparison.Ordinal) ||
            string.Equals(updated.State, "startPending", StringComparison.Ordinal)
            ? updated
            : throw UnexpectedState(serviceName, updated.State, "running");
    }

    public async Task<WindowsServiceSnapshot> StopAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        var current = query.GetStatus(serviceName);
        if (!current.Installed)
        {
            throw NotFound(serviceName);
        }
        if (string.Equals(current.State, "stopped", StringComparison.Ordinal))
        {
            return current;
        }

        var result = await commandRunner.RunAsync(
            ["stop", serviceName],
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, serviceName, ErrorServiceNotActive);
        var updated = query.GetStatus(serviceName);
        return string.Equals(updated.State, "stopped", StringComparison.Ordinal) ||
            string.Equals(updated.State, "stopPending", StringComparison.Ordinal)
            ? updated
            : throw UnexpectedState(serviceName, updated.State, "stopped");
    }

    public async Task<WindowsServiceSnapshot> UninstallAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        var current = query.GetStatus(serviceName);
        if (!current.Installed)
        {
            return current;
        }
        if (!string.Equals(current.State, "stopped", StringComparison.Ordinal))
        {
            await StopAsync(serviceName, cancellationToken).ConfigureAwait(false);
        }

        var result = await commandRunner.RunAsync(
            ["delete", serviceName],
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, serviceName, ErrorServiceDoesNotExist);
        return query.GetStatus(serviceName);
    }

    private static void EnsureSuccess(
        WindowsServiceCommandResult result,
        string serviceName,
        int acceptedExitCode)
    {
        if (result.ExitCode == 0 || result.ExitCode == acceptedExitCode)
        {
            return;
        }
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new VaultException(
            VaultErrorCode.StorageError,
            string.IsNullOrWhiteSpace(detail)
                ? $"Windows service operation failed for {serviceName}."
                : $"Windows service operation failed for {serviceName}: {detail}");
    }

    private static VaultException NotFound(string serviceName)
    {
        return new VaultException(
            VaultErrorCode.NotFound,
            $"Windows service was not found: {serviceName}");
    }

    private static VaultException UnexpectedState(
        string serviceName,
        string actual,
        string expected)
    {
        return new VaultException(
            VaultErrorCode.StorageError,
            $"Windows service {serviceName} entered {actual} instead of {expected}.");
    }
}
