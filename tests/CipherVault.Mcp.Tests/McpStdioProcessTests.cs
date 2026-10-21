using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CipherVault.Mcp.Tests;

public sealed class McpStdioProcessTests
{
    [Fact]
    public async Task RealToolCallReturnsHostUnavailableWhenHostIsNotRunning()
    {
        using var fixture = new ProcessFixture(accessMode: "readWrite");
        await using var server = McpServerProcess.Start(fixture.ConfigPath);
        using var initialize = await server.RequestAsync(
            1,
            "initialize",
            new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new
                {
                    name = "CipherVault.Mcp.Tests",
                    version = "1.0.0"
                }
            });
        await server.NotifyAsync("notifications/initialized");

        using var status = await server.RequestAsync(
            2,
            "tools/call",
            new
            {
                name = "vault_status",
                arguments = new { }
            });
        using var statusDocument = JsonDocument.Parse(GetToolText(status));
        Assert.False(statusDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "host_unavailable",
            statusDocument.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());

        await server.CloseInputAndWaitAsync();
        Assert.Equal(0, server.ExitCode);
    }

    [Fact]
    public async Task ProcessRejectsMissingExplicitConfigWithoutWritingProtocolOutput()
    {
        using var fixture = new ProcessFixture();
        var missingConfig = Path.Combine(fixture.Root, "missing.json");
        var executable = Path.Combine(AppContext.BaseDirectory, "CipherVault.Mcp.exe");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(missingConfig);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start CipherVault.Mcp.exe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(1, process.ExitCode);
        Assert.Empty(await stdoutTask);
        Assert.Contains("InvalidInput", await stderrTask, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessListsToolsAndReportsHostUnavailable()
    {
        using var fixture = new ProcessFixture();
        await using var server = McpServerProcess.Start(fixture.ConfigPath);

        using var initialize = await server.RequestAsync(
            1,
            "initialize",
            new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new
                {
                    name = "CipherVault.Mcp.Tests",
                    version = "1.0.0"
                }
            });
        Assert.Equal(
            "2025-11-25",
            initialize.RootElement
                .GetProperty("result")
                .GetProperty("protocolVersion")
                .GetString());

        await server.NotifyAsync("notifications/initialized");

        using var tools = await server.RequestAsync(2, "tools/list", new { });
        var toolNames = tools.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("vault_status", toolNames);
        Assert.Contains("list_catalog", toolNames);
        Assert.Contains("create_note", toolNames);

        using var status = await server.RequestAsync(
            3,
            "tools/call",
            new
            {
                name = "vault_status",
                arguments = new { }
            });
        var statusText = status.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString();
        using var statusDocument = JsonDocument.Parse(statusText!);
        Assert.False(statusDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "host_unavailable",
            statusDocument.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());

        using var blockedWrite = await server.RequestAsync(
            4,
            "tools/call",
            new
            {
                name = "create_note",
                arguments = new
                {
                    title = "blocked",
                    content = "must not be written"
                }
            });
        var blockedText = blockedWrite.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString();
        using var blockedDocument = JsonDocument.Parse(blockedText!);
        Assert.Equal(
            "read_only",
            blockedDocument.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());

        await server.CloseInputAndWaitAsync();

        Assert.Equal(0, server.ExitCode);
        Assert.NotEmpty(server.StdoutLines);
        foreach (var line in server.StdoutLines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(
                "2.0",
                document.RootElement.GetProperty("jsonrpc").GetString());
        }
    }

    private sealed class ProcessFixture : IDisposable
    {
        public ProcessFixture(string accessMode = "readOnly")
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-mcp-process-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            ConfigPath = Path.Combine(Root, "config.json");
            var port = ReservePort();
            File.WriteAllText(
                ConfigPath,
                JsonSerializer.Serialize(new
                {
                    configVersion = 2,
                    vaultPath = Path.Combine(Root, "vault"),
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
        }

        public string Root { get; }
        public string ConfigPath { get; }
        private static int ReservePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static string GetToolText(JsonDocument response)
    {
        return response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            ?? throw new InvalidOperationException("Tool result text was empty.");
    }

    private sealed class McpServerProcess : IAsyncDisposable
    {
        private static readonly string[] CipherVaultEnvironmentVariables =
        [
            "CIPHER_VAULT_CONFIG",
            "CIPHER_VAULT_PATH",
            "CIPHER_VAULT_IDLE_MINUTES",
            "CIPHER_VAULT_HARD_HOURS",
            "CIPHER_VAULT_ACCESS_MODE",
            "CIPHER_VAULT_READ_ONLY",
            "CIPHER_VAULT_MCP_TOKEN"
        ];

        private readonly Process _process;
        private readonly StreamWriter _input;
        private readonly StreamReader _output;
        private readonly Task _stderrTask;
        private readonly List<string> _stderrLines = [];

        private McpServerProcess(
            Process process,
            StreamWriter input,
            StreamReader output,
            Task stderrTask,
            List<string> stderrLines)
        {
            _process = process;
            _input = input;
            _output = output;
            _stderrTask = stderrTask;
            _stderrLines = stderrLines;
        }

        public List<string> StdoutLines { get; } = [];
        public int ExitCode => _process.ExitCode;

        public static McpServerProcess Start(string configPath)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "CipherVault.Mcp.exe");
            Assert.True(File.Exists(executable), $"MCP executable was not found: {executable}");
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
            foreach (var variable in CipherVaultEnvironmentVariables)
            {
                startInfo.Environment.Remove(variable);
            }
            startInfo.Environment["CIPHER_VAULT_MCP_TOKEN"] = "test-agent-token";

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start CipherVault.Mcp.exe.");
            var stderrLines = new List<string>();
            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    lock (stderrLines)
                    {
                        stderrLines.Add(line);
                    }
                }
            });
            return new McpServerProcess(
                process,
                process.StandardInput,
                process.StandardOutput,
                stderrTask,
                stderrLines);
        }

        public async Task<JsonDocument> RequestAsync(
            int id,
            string method,
            object parameters)
        {
            await WriteAsync(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters
            });
            while (true)
            {
                var line = await ReadLineAsync();
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException(
                        $"MCP stdout contained non-JSON output: {line}",
                        exception);
                }
                StdoutLines.Add(line);
                if (document.RootElement.TryGetProperty("id", out var responseId) &&
                    responseId.TryGetInt32(out var numericId) &&
                    numericId == id)
                {
                    return document;
                }
                document.Dispose();
            }
        }

        public Task NotifyAsync(string method)
        {
            return WriteAsync(new
            {
                jsonrpc = "2.0",
                method
            });
        }

        public async Task CloseInputAndWaitAsync()
        {
            _input.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
                throw new TimeoutException("CipherVault.Mcp.exe did not exit after stdin was closed.");
            }
            await _stderrTask;
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
            await _stderrTask;
            _process.Dispose();
        }

        private async Task<string> ReadLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var line = await _output.ReadLineAsync(timeout.Token);
                if (line is not null)
                {
                    return line;
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Timed out waiting for MCP stdout.");
            }

            string stderr;
            lock (_stderrLines)
            {
                stderr = string.Join(Environment.NewLine, _stderrLines);
            }
            throw new InvalidOperationException(
                $"CipherVault.Mcp.exe exited before returning a response. stderr: {stderr}");
        }

        private async Task WriteAsync(object message)
        {
            await _input.WriteLineAsync(JsonSerializer.Serialize(message));
            await _input.FlushAsync();
        }
    }
}
