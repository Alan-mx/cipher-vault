using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CipherVault.Auth.Win;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;

namespace CipherVault.Mcp.Tests;

public sealed class McpStdioProcessTests
{
    private const string PromptPassword = "mcp prompt password";

    [Fact]
    public async Task RealToolCallPromptsForPasswordAndReturnsDocument()
    {
        if (!Environment.UserInteractive)
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        using var fixture = new ProcessFixture(accessMode: "readWrite");
        await fixture.InitializeVaultAsync();
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

        var createTask = server.RequestAsync(
            2,
            "tools/call",
            new
            {
                name = "create_note",
                arguments = new
                {
                    title = "real prompt document",
                    content = "content through the real Windows prompt",
                    folder = "integration"
                }
            });
        var promptWindow = await WaitForWindowAsync("CipherVault 授权");
        FocusWindow(promptWindow);
        SubmitPassword(promptWindow, PromptPassword);
        using var created = await createTask.WaitAsync(TimeSpan.FromSeconds(15));
        var createdText = GetToolText(created);
        using var createdDocument = JsonDocument.Parse(createdText);
        Assert.True(createdDocument.RootElement.GetProperty("ok").GetBoolean());
        var documentId = createdDocument.RootElement
            .GetProperty("document")
            .GetProperty("documentId")
            .GetGuid();

        using var read = await server.RequestAsync(
            3,
            "tools/call",
            new
            {
                name = "read_note",
                arguments = new
                {
                    documentId = documentId.ToString("D")
                }
            });
        using var readDocument = JsonDocument.Parse(GetToolText(read));
        Assert.Equal(
            "content through the real Windows prompt",
            readDocument.RootElement
                .GetProperty("document")
                .GetProperty("content")
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
    public async Task ProcessInitializesListsToolsAndReturnsVaultStatus()
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
        Assert.True(statusDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "readOnly",
            statusDocument.RootElement
                .GetProperty("vault")
                .GetProperty("accessMode")
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
            File.WriteAllText(
                ConfigPath,
                JsonSerializer.Serialize(new
                {
                    configVersion = 1,
                    vaultPath = Path.Combine(Root, "vault"),
                    session = new
                    {
                        idleMinutes = 15,
                        hardHours = 4
                    },
                    mcp = new
                    {
                        accessMode
                    }
                }));
        }

        public string Root { get; }
        public string ConfigPath { get; }
        public string VaultPath => Path.Combine(Root, "vault");

        public async Task InitializeVaultAsync()
        {
            await using var service = new VaultService(
                new AlwaysActiveActivityProvider());
            var initialized = await service.InitializeAsync(VaultPath, PromptPassword);
            await service.ConfirmRecoveryKeyAsync(
                VaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
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
        _ = NativeMethods.PostMessage(
            window,
            0x0100,
            (IntPtr)0x0D,
            IntPtr.Zero);
        _ = NativeMethods.PostMessage(
            window,
            0x0101,
            (IntPtr)0x0D,
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

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
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
            "CIPHER_VAULT_READ_ONLY"
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
