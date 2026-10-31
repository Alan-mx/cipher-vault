using System.Net;
using System.Net.Sockets;
using CipherVault.Application.Vault;
using CipherVault.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace CipherVault.Server.Tests;

internal sealed class ServerTestApplication : IAsyncDisposable
{
    internal const string Password = "correct horse battery staple";

    private readonly ServerInstanceLock instanceLock;
    private readonly VaultSessionManager manager;
    private readonly WebApplication application;

    private ServerTestApplication(
        string parent,
        ManualTimeProvider timeProvider,
        ServerOptions options,
        ServerInstanceLock instanceLock,
        VaultSessionManager manager,
        WebApplication application,
        HttpClient client)
    {
        Parent = parent;
        TimeProvider = timeProvider;
        Options = options;
        this.instanceLock = instanceLock;
        this.manager = manager;
        this.application = application;
        Client = client;
    }

    internal string Parent { get; }

    internal ManualTimeProvider TimeProvider { get; }

    internal ServerOptions Options { get; }

    internal VaultSessionManager Manager => manager;

    internal HttpClient Client { get; }

    internal HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = new CookieContainer()
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(
                $"http://127.0.0.1:{Options.ListenAddresses[0].Port}")
        };
    }

    internal static async Task<ServerTestApplication> CreateAsync(
        bool initializeVault = false,
        bool unlockVault = false,
        TimeSpan? idleTimeout = null,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-server-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        var paths = ApplicationPaths.Create(
            Path.Combine(parent, "data"),
            Path.Combine(parent, "config"));
        paths.EnsureCreated();
        var options = new ServerOptions(
            paths,
            [new ServerListenAddress("127.0.0.1", FindFreePort())],
            idleTimeout ?? TimeSpan.FromMinutes(10),
            ServerOptions.DefaultMaxRequestBodyBytes);
        var timeProvider = new ManualTimeProvider();
        var instanceLock = ServerInstanceLock.Acquire(paths.ServerLockPath);
        VaultSessionManager? manager = null;
        WebApplication? application = null;
        HttpClient? client = null;
        try
        {
            manager = await VaultSessionManager.CreateAsync(
                paths.VaultDirectory,
                new VaultSessionOptions(options.IdleTimeout),
                timeProvider);
            if (initializeVault)
            {
                _ = await manager.InitializeAsync(Password);
            }
            if (unlockVault)
            {
                _ = await manager.UnlockAsync(Password);
            }

            application = await ServerWebApplication.BuildAsync(
                options,
                manager,
                timeProvider,
                configureLogging);
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CookieContainer = new CookieContainer()
            };
            client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{options.ListenAddresses[0].Port}")
            };
            return new ServerTestApplication(
                parent,
                timeProvider,
                options,
                instanceLock,
                manager,
                application,
                client);
        }
        catch
        {
            client?.Dispose();
            if (application is not null)
            {
                await application.DisposeAsync();
            }
            else if (manager is not null)
            {
                await manager.DisposeAsync();
            }
            await instanceLock.DisposeAsync();
            await DeleteBestEffortAsync(parent);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
        await instanceLock.DisposeAsync();
        await DeleteBestEffortAsync(Parent);
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task DeleteBestEffortAsync(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                await Task.Delay(50);
            }
        }
    }
}
