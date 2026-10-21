using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;

namespace CipherVault.Mcp.Tests;

public sealed class McpBridgeHostTests
{
    private const string Password = "mcp bridge host password";

    [Fact]
    public async Task BridgeForwardsToolsToRunningHost()
    {
        using var hostLock = HostIntegrationTestLock.Acquire();
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-mcp-bridge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Process? host = null;
        StdioProcess? bridge = null;
        try
        {
            var port = ReservePort();
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

            var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var tokenHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
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
                    clients = new[]
                    {
                        new
                        {
                            id = "bridge-agent",
                            tokenHash = "sha256:" + tokenHash,
                            accessMode = "readOnly",
                            allowLock = false,
                            enabled = true
                        }
                    }
                }));

            host = StartHost(configPath);
            await WaitForPortAsync(port, TimeSpan.FromSeconds(15));
            bridge = StdioProcess.Start(configPath, rawToken);
            using var initialize = await bridge.RequestAsync(
                1,
                "initialize",
                new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "bridge-host-test",
                        version = "1.0.0"
                    }
                });
            await bridge.NotifyAsync("notifications/initialized");

            using var status = await bridge.RequestAsync(
                2,
                "tools/call",
                new
                {
                    name = "vault_status",
                    arguments = new { }
                });
            var text = status.RootElement
                .GetProperty("result")
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString();
            Assert.False(string.IsNullOrWhiteSpace(text));
            using var payload = JsonDocument.Parse(text);
            Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "bridge-agent",
                payload.RootElement
                    .GetProperty("vault")
                    .GetProperty("clientId")
                    .GetString());

            await bridge.CloseAsync();
            bridge = null;
        }
        finally
        {
            if (bridge is not null)
            {
                await bridge.DisposeAsync();
            }
            if (host is not null)
            {
                if (!host.HasExited)
                {
                    host.Kill(entireProcessTree: true);
                    await host.WaitForExitAsync();
                }
                host.Dispose();
            }
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    private static Process StartHost(string configPath)
    {
        var executable = Path.Combine(
            Path.GetDirectoryName(FindCliExecutable())!,
            "CipherVault.Host.exe");
        Assert.True(File.Exists(executable), $"Host executable was not found: {executable}");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(configPath)!
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start CipherVault.Host.exe.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException("CipherVault Host did not start.");
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

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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

    private sealed class StdioProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _input;
        private readonly StreamReader _output;
        private readonly Task<string> _stderr;

        private StdioProcess(
            Process process,
            StreamWriter input,
            StreamReader output,
            Task<string> stderr)
        {
            _process = process;
            _input = input;
            _output = output;
            _stderr = stderr;
        }

        public static StdioProcess Start(string configPath, string token)
        {
            var executable = Path.Combine(
                AppContext.BaseDirectory,
                "CipherVault.Mcp.exe");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(configPath)!
            };
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(configPath);
            startInfo.Environment["CIPHER_VAULT_MCP_TOKEN"] = token;
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start CipherVault.Mcp.exe.");
            return new StdioProcess(
                process,
                process.StandardInput,
                process.StandardOutput,
                process.StandardError.ReadToEndAsync());
        }

        public async Task<JsonDocument> RequestAsync(
            int id,
            string method,
            object parameters)
        {
            await _input.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters
            }));
            await _input.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var line = await _output.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException(
                    $"MCP bridge exited: {await _stderr}");
            return JsonDocument.Parse(line);
        }

        public Task NotifyAsync(string method)
        {
            return _input.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method
            }));
        }

        public async Task CloseAsync()
        {
            _input.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, _process.ExitCode);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            _input.Dispose();
            _output.Dispose();
            _process.Dispose();
        }
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
