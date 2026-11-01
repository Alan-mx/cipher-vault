using System.Net;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CipherVault.Phase8.ProcessProbe;

internal static class Program
{
    private static readonly TimeSpan ShutdownTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ProbeOptions.Parse(args);
            return options.Command switch
            {
                "run-server" => await RunServerAsync(options),
                "restore" => await RunRestoreAsync(options),
                _ => Usage()
            };
        }
        catch (ProbeException exception)
        {
            Console.Error.WriteLine(exception.SafeCode);
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("phase8_probe_internal_error");
            return 1;
        }
    }

    private static async Task<int> RunServerAsync(ProbeOptions options)
    {
        RequireFile(options.Server);
        RequireDirectories(
            options.DataDirectory,
            options.ConfigDirectory,
            options.BackupDirectory);

        await using var first = await StartAndWaitAsync(options);
        var firstState = await ReadStateAsync(options.BaseUrl);
        await WriteJsonAsync(
            options.ReadyFile,
            new
            {
                state = firstState,
                pid = first.ProcessId
            });
        await WaitForFileAsync(options.ContinueFile, options.Timeout);
        await first.RequestGracefulShutdownAsync();
        await RequireCleanExitAsync(first, "first_shutdown_failed");
        await RequireLockAvailableAsync(options.ConfigDirectory);

        await using var second = await StartAndWaitAsync(options);
        var secondState = await ReadStateAsync(options.BaseUrl);
        if (!string.Equals(secondState, "locked", StringComparison.Ordinal))
        {
            throw new ProbeException("restart_state_not_locked");
        }
        await second.RequestGracefulShutdownAsync();
        await RequireCleanExitAsync(second, "second_shutdown_failed");
        await RequireLockAvailableAsync(options.ConfigDirectory);

        await WriteJsonAsync(
            options.EvidenceFile,
            new
            {
                platform = Environment.OSVersion.Platform.ToString(),
                os = System.Runtime.InteropServices.RuntimeInformation
                    .OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation
                    .ProcessArchitecture.ToString(),
                firstShutdownExitCode = 0,
                secondShutdownExitCode = 0,
                restartState = "locked",
                lockReacquired = true,
                passed = true
            });
        Console.Out.WriteLine("phase8_process_probe_passed");
        return 0;
    }

    private static async Task<int> RunRestoreAsync(ProbeOptions options)
    {
        RequireFile(options.Server);
        RequireFile(options.Package);
        if (string.IsNullOrWhiteSpace(options.Target))
        {
            throw new ProbeException("restore_target_missing");
        }
        if (Directory.Exists(options.Target) || File.Exists(options.Target))
        {
            throw new ProbeException("restore_target_already_exists");
        }
        var targetParent = Path.GetDirectoryName(
            Path.GetFullPath(options.Target));
        if (string.IsNullOrWhiteSpace(targetParent)
            || !Directory.Exists(targetParent))
        {
            throw new ProbeException("restore_target_parent_missing");
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new ProbeException("restore_probe_windows_only");
        }

        var password = await ReadRestorePasswordAsync(options);
        if (string.IsNullOrEmpty(password))
        {
            throw new ProbeException("restore_password_missing");
        }

        try
        {
            var environment = ProbeEnvironment.Create(
                options.DataDirectory,
                options.ConfigDirectory,
                options.BackupDirectory,
                options.BaseUrl);
            await using var session = WindowsPseudoConsole.Start(
                options.Server,
                [
                    "--restore-backup",
                    options.Package,
                    "--target",
                    options.Target
                ],
                environment);
            await session.WaitForOutputAsync(
                "Backup password:",
                options.Timeout);
            await session.WriteSecretAsync(password);
            var exitCode = await session.WaitForExitAsync(options.Timeout);
            if (exitCode != 0 || !Directory.Exists(options.Target))
            {
                throw new ProbeException("restore_failed");
            }

            await WriteJsonAsync(
                options.EvidenceFile,
                new
                {
                    targetPublished = true,
                    passed = true
                });
            Console.Out.WriteLine("phase8_restore_probe_passed");
            return 0;
        }
        finally
        {
            password = string.Empty;
        }
    }

    private static async Task<string?> ReadRestorePasswordAsync(
        ProbeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PasswordPipe))
        {
            return Console.In.ReadLine();
        }
        await using var pipe = new NamedPipeClientStream(
            ".",
            options.PasswordPipe,
            PipeDirection.In,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        return await reader.ReadLineAsync();
    }

    private static async Task<ServerProcess> StartAndWaitAsync(
        ProbeOptions options)
    {
        var environment = ProbeEnvironment.Create(
            options.DataDirectory,
            options.ConfigDirectory,
            options.BackupDirectory,
            options.BaseUrl);
        ServerProcess process = OperatingSystem.IsWindows()
            ? WindowsPseudoConsole.Start(options.Server, [], environment)
            : DetachedServerProcess.Start(options.Server, environment);
        try
        {
            var deadline = DateTimeOffset.UtcNow + options.Timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    _ = await ReadStateAsync(options.BaseUrl);
                    return process;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(200);
                }
                catch (TaskCanceledException)
                {
                    await Task.Delay(200);
                }
            }
        }
        catch
        {
            await process.TerminateAsync();
            await process.DisposeAsync();
            throw;
        }

        await process.TerminateAsync();
        await process.DisposeAsync();
        throw new ProbeException("server_not_ready");
    }

    private static async Task<string> ReadStateAsync(string baseUrl)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        using var response = await client.GetAsync(
            $"{baseUrl}/api/vault/status");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException("status_not_ready");
        }
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("state").GetString()
            ?? string.Empty;
    }

    private static async Task RequireCleanExitAsync(
        ServerProcess process,
        string errorCode)
    {
        var exitCode = await process.WaitForExitAsync(ShutdownTimeout);
        if (exitCode != 0)
        {
            throw new ProbeException(errorCode);
        }
    }

    private static Task RequireLockAvailableAsync(string configDirectory)
    {
        var lockPath = Path.Combine(configDirectory, "server.lock");
        try
        {
            using var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return Task.CompletedTask;
        }
        catch (IOException)
        {
            throw new ProbeException("server_lock_not_released");
        }
    }

    private static async Task WaitForFileAsync(
        string? path,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ProbeException("continue_file_required");
        }
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return;
            }
            await Task.Delay(200);
        }
        throw new ProbeException("continue_file_timeout");
    }

    private static void RequireFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new ProbeException("required_file_missing");
        }
    }

    private static void RequireDirectories(params string[] paths)
    {
        if (paths.Any(path =>
                string.IsNullOrWhiteSpace(path)
                || !Directory.Exists(path)))
        {
            throw new ProbeException("required_directory_missing");
        }
    }

    private static async Task WriteJsonAsync(
        string? path,
        object value)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(value, EvidenceJson),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "phase8_probe_usage: run-server|restore with named options");
        return 2;
    }

}

internal sealed class ProbeException(string safeCode) : Exception(safeCode)
{
    public string SafeCode { get; } = safeCode;
}
