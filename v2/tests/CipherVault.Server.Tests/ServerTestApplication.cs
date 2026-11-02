using System.Net;
using System.Net.Sockets;
using CipherVault.Application.Vault;
using CipherVault.Server.Hosting;
using CipherVault.Server.Mcp;
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
        McpTokenStore tokenStore,
        ServerInstanceLock instanceLock,
        VaultSessionManager manager,
        WebApplication application,
        HttpClient client)
    {
        Parent = parent;
        TimeProvider = timeProvider;
        Options = options;
        TokenStore = tokenStore;
        this.instanceLock = instanceLock;
        this.manager = manager;
        this.application = application;
        Client = client;
    }

    internal string Parent { get; }

    internal ManualTimeProvider TimeProvider { get; }

    internal ServerOptions Options { get; }

    internal McpTokenStore TokenStore { get; }

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
        Action<ILoggingBuilder>? configureLogging = null,
        VaultSessionTimingOptions? timingOptions = null,
        GitBackupOptions? gitBackup = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CreateCoreAsync(
                    initializeVault,
                    unlockVault,
                    idleTimeout,
                    configureLogging,
                    timingOptions,
                    gitBackup);
            }
            catch (Exception exception) when (
                attempt < 3 && IsAddressInUse(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(
                    20 * (attempt + 1)));
            }
        }
    }

    private static async Task<ServerTestApplication> CreateCoreAsync(
        bool initializeVault,
        bool unlockVault,
        TimeSpan? idleTimeout,
        Action<ILoggingBuilder>? configureLogging,
        VaultSessionTimingOptions? timingOptions,
        GitBackupOptions? gitBackup)
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-server-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        var paths = ApplicationPaths.Create(
            Path.Combine(parent, "data"),
            Path.Combine(parent, "config"));
        paths.EnsureCreated();
        if (gitBackup is not null)
        {
            gitBackup = gitBackup with
            {
                SecureHooksPath =
                    gitBackup.SecureHooksPath ?? paths.GitHooksDirectory,
                SecureAttributesPath =
                    gitBackup.SecureAttributesPath ?? paths.GitAttributesFile
            };
        }
        var options = new ServerOptions(
            paths,
            [new ServerListenAddress("127.0.0.1", FindFreePort())],
            idleTimeout ?? TimeSpan.FromMinutes(10),
            ServerOptions.DefaultMaxRequestBodyBytes,
            gitBackup: gitBackup);
        var timeProvider = new ManualTimeProvider();
        var instanceLock = ServerInstanceLock.Acquire(paths.ServerLockPath);
        VaultSessionManager? manager = null;
        WebApplication? application = null;
        HttpClient? client = null;
        try
        {
            var sessionOptions = new VaultSessionOptions(options.IdleTimeout);
            manager = timingOptions is null
                ? await VaultSessionManager.CreateAsync(
                    paths.VaultDirectory,
                    sessionOptions,
                    timeProvider)
                : await VaultSessionManager.CreateForTestAsync(
                    paths.VaultDirectory,
                    sessionOptions,
                    timingOptions,
                    timeProvider);
            var tokenStore = await McpTokenStore.LoadAsync(
                paths.McpTokenPath,
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
                tokenStore,
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
                tokenStore,
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
        await manager.DisposeAsync();
        await instanceLock.DisposeAsync();
        await DeleteBestEffortAsync(
            Parent,
            allowResidualVaultLock:
                manager.State == VaultSessionState.LockFailed);
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is SocketException
                {
                    SocketErrorCode: SocketError.AddressAlreadyInUse
                })
            {
                return true;
            }
        }
        return false;
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

    private static async Task DeleteBestEffortAsync(
        string path,
        bool allowResidualVaultLock = false)
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
            catch (IOException) when (allowResidualVaultLock)
            {
                // LockFailed is a restart boundary; the OS releases native
                // database handles when the test host exits.
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                await Task.Delay(50);
            }
        }
    }
}
