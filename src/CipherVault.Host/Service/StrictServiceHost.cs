using System.IO;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HostApplicationBuilderFactory = Microsoft.Extensions.Hosting.Host;

namespace CipherVault.Host.Service;

public sealed class StrictServiceHost : IAsyncDisposable
{
    public const string ServiceName = "CipherVaultHost";

    private readonly IHost _host;
    private readonly MachineExclusiveFileLock _vaultOwnerLock;
    private readonly MachineExclusiveFileLock _hostInstanceLock;
    private bool _disposed;

    private StrictServiceHost(
        IHost host,
        MachineExclusiveFileLock vaultOwnerLock,
        MachineExclusiveFileLock hostInstanceLock)
    {
        _host = host;
        _vaultOwnerLock = vaultOwnerLock;
        _hostInstanceLock = hostInstanceLock;
    }

    public IServiceProvider Services => _host.Services;

    public static StrictServiceHost Create(
        CipherVaultServiceConfigFile configuration,
        CipherVaultServicePaths paths)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        CipherVaultServiceConfiguration.Validate(configuration);
        if (CipherVaultServiceConfiguration.ParseSecurityMode(
                configuration.SecurityMode) != HostSecurityMode.Strict)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "The Windows service host requires strict security mode.");
        }
        if (!PathsEqual(configuration.DataRoot, paths.DataRoot) ||
            !PathsEqual(configuration.VaultPath, paths.VaultPath))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Strict service paths must match the installed ProgramData layout.");
        }

        var hostInstanceLock = MachineExclusiveFileLock.Acquire(
            paths.HostInstanceLockPath);
        MachineExclusiveFileLock? vaultOwnerLock = null;
        IHost? host = null;
        try
        {
            vaultOwnerLock = MachineExclusiveFileLock.Acquire(
                paths.VaultOwnerLockPath);
            var builder = HostApplicationBuilderFactory.CreateApplicationBuilder(
                new HostApplicationBuilderSettings
                {
                    Args = [],
                    ContentRootPath = AppContext.BaseDirectory
                });
            builder.Services.AddWindowsService(options =>
                options.ServiceName = ServiceName);
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(paths);
            host = builder.Build();
            return new StrictServiceHost(
                host,
                vaultOwnerLock,
                hostInstanceLock);
        }
        catch
        {
            host?.Dispose();
            vaultOwnerLock?.Dispose();
            hostInstanceLock.Dispose();
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _host.StartAsync(cancellationToken);
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _host.RunAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _host.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _host.Dispose();
            }
            finally
            {
                _vaultOwnerLock.Dispose();
                _hostInstanceLock.Dispose();
            }
        }
    }

    private static bool PathsEqual(string? left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left ?? string.Empty)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
