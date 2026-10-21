using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Auth.Win;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;

namespace CipherVault.Mcp.Tests;

public sealed class CliOnlineHostTests
{
    private const string Password = "cli online host password";

    [Fact]
    public async Task CliStatusStartsHostAndUsesAdminApi()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-cli-online-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var port = ReservePort();
        try
        {
            var vaultPath = Path.Combine(root, "vault");
            await using (var service = new VaultService(
                             new AlwaysActiveActivityProvider()))
            {
                var initialized = await service.InitializeAsync(vaultPath, Password);
                await service.ConfirmRecoveryKeyAsync(
                    vaultPath,
                    initialized.RecoveryKey,
                    VaultActorKind.UserUi);
            }

            var configPath = Path.Combine(root, "config.json");
            await File.WriteAllTextAsync(
                configPath,
                JsonSerializer.Serialize(new
                {
                    configVersion = 2,
                    vaultPath,
                    session = new
                    {
                        idleMinutes = 15,
                        hardHours = 4
                    },
                    host = new
                    {
                        bindAddress = "127.0.0.1",
                        port,
                        mcpPath = "/mcp",
                        adminPath = "/admin/v1",
                        maxRequestBodyBytes = 4 * 1024 * 1024,
                        allowedOrigins = Array.Empty<string>()
                    },
                    clients = Array.Empty<object>()
                }));

            var result = await RunCliAsync(
                FindCliExecutable(),
                "status",
                "--config",
                configPath);

            Assert.True(
                result.ExitCode == 0,
                $"stdout: {result.Stdout}{Environment.NewLine}stderr: {result.Stderr}");
            using var document = JsonDocument.Parse(result.Stdout);
            Assert.True(document.RootElement.GetProperty("initialized").GetBoolean());
            Assert.Equal(
                vaultPath,
                document.RootElement.GetProperty("vaultPath").GetString());
        }
        finally
        {
            await StopHostAsync(port);
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    private static async Task<ProcessResult> RunCliAsync(
        string cliPath,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start CipherVault.Cli.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException(
                $"CLI timed out. stdout: {await stdout}{Environment.NewLine}" +
                $"stderr: {await stderr}");
        }
        return new ProcessResult(
            process.ExitCode,
            await stdout,
            await stderr);
    }

    private static async Task StopHostAsync(int port)
    {
        try
        {
            var token = ReadAdminToken();
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(5)
            };
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/admin/v1/host/stop")
            {
                Content = JsonContent.Create(new HostStopRequest("cli-online-test"))
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                token);
            using var response = await client.SendAsync(request);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var health = await client.GetAsync("/admin/v1/health");
                    await Task.Delay(100);
                }
                catch (HttpRequestException)
                {
                    return;
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }
        catch (HttpRequestException)
        {
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ReadAdminToken()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CipherVault",
            "admin-token.bin");
        var protectedBytes = File.ReadAllBytes(path);
        var plainBytes = ProtectedData.Unprotect(
            protectedBytes,
            Encoding.UTF8.GetBytes("CipherVault.Host.AdminToken.v1"),
            DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 49)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 49)
            {
                await Task.Delay(100);
            }
        }
    }

    private static string FindCliExecutable()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name
            ?? throw new InvalidOperationException("Unable to resolve build configuration.");
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var executable = Path.Combine(
            repositoryRoot,
            "src",
            "CipherVault.Cli",
            "bin",
            configuration,
            "net10.0-windows",
            "CipherVault.Cli.exe");
        Assert.True(File.Exists(executable), $"CLI executable was not found: {executable}");
        return executable;
    }

    private sealed record ProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr);

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
