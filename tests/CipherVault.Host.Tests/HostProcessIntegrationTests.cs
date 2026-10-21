using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CipherVault.Auth.Win;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Client;
using CipherVault.Host.Client.Security;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class HostProcessIntegrationTests
{
    private const string Password = "host process integration password";

    [Fact]
    public async Task RealHostEnforcesSingleInstanceAndRestartsLocked()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-host-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Process? first = null;
        Process? restarted = null;
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

            first = StartHost(configPath);
            var token = await new DpapiAdminTokenStore().GetTokenAsync(
                HostAdminClientKind.Ui);
            await WaitForHealthAsync(port, token, TimeSpan.FromSeconds(15));
            var initialStatus = await GetStatusAsync(port, token);
            Assert.Equal("locked", initialStatus.State);

            var wrongConfiguration = new CipherVault.Core.Configuration
                .ResolvedCipherVaultHostConfig(
                Path.Combine(root, "different-vault"),
                VaultSessionOptions.Default,
                new CipherVault.Core.Configuration.HostEndpointOptions(
                    "127.0.0.1",
                    port,
                    "/mcp",
                    "/admin/v1",
                    4 * 1024 * 1024,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                [],
                Path.Combine(root, "different-config.json"));
            var mismatch = await Assert.ThrowsAsync<VaultException>(() =>
                HttpHostVaultClient.CreateAsync(
                    wrongConfiguration,
                    HostAdminClientKind.Ui));
            Assert.Equal(VaultErrorCode.Conflict, mismatch.Code);

            using (var second = StartHost(configPath))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await second.WaitForExitAsync(timeout.Token);
                Assert.NotEqual(0, second.ExitCode);
            }

            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync();
            first.Dispose();
            first = null;

            restarted = StartHost(configPath);
            await WaitForHealthAsync(port, token, TimeSpan.FromSeconds(15));
            var restartedStatus = await GetStatusAsync(port, token);
            Assert.Equal("locked", restartedStatus.State);
            Assert.Null(restartedStatus.SessionId);
        }
        finally
        {
            await StopProcessAsync(first);
            await StopProcessAsync(restarted);
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    private static Process StartHost(string configPath)
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
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

    private static async Task WaitForHealthAsync(
        int port,
        string token,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        using var client = CreateClient(port);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var request = CreateAdminRequest(
                    HttpMethod.Get,
                    "/admin/v1/health",
                    token);
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("CipherVault Host did not become healthy.");
    }

    private static async Task<HostSessionStatusData> GetStatusAsync(
        int port,
        string token)
    {
        using var client = CreateClient(port);
        using var request = CreateAdminRequest(
            HttpMethod.Get,
            "/admin/v1/status",
            token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostStatusData>>(new JsonSerializerOptions(
                JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        return payload.Data.Session;
    }

    private static HttpClient CreateClient(int port)
    {
        return new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private static HttpRequestMessage CreateAdminRequest(
        HttpMethod method,
        string path,
        string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token);
        return request;
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            process.Dispose();
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

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
