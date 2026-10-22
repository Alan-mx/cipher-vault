using System.ComponentModel;
using System.IO;
using System.ServiceProcess;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;

namespace CipherVault.Host.ServiceManagement;

public sealed record WindowsServiceSnapshot(
    bool Installed,
    string State,
    string? StartMode);

public interface IWindowsServiceQuery
{
    WindowsServiceSnapshot GetStatus(string serviceName);
}

public sealed class WindowsServiceQuery : IWindowsServiceQuery
{
    public WindowsServiceSnapshot GetStatus(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        using var controller = new ServiceController(serviceName);
        try
        {
            controller.Refresh();
            return new WindowsServiceSnapshot(
                Installed: true,
                ToState(controller.Status),
                controller.StartType.ToString());
        }
        catch (InvalidOperationException exception) when (
            exception.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            return new WindowsServiceSnapshot(
                Installed: false,
                State: "notInstalled",
                StartMode: null);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                $"Windows service status could not be read: {exception.Message}",
                exception);
        }
    }

    private static string ToState(ServiceControllerStatus status)
    {
        return status switch
        {
            ServiceControllerStatus.Stopped => "stopped",
            ServiceControllerStatus.StartPending => "startPending",
            ServiceControllerStatus.StopPending => "stopPending",
            ServiceControllerStatus.Running => "running",
            ServiceControllerStatus.ContinuePending => "continuePending",
            ServiceControllerStatus.PausePending => "pausePending",
            ServiceControllerStatus.Paused => "paused",
            _ => "unknown"
        };
    }
}

public sealed record HostServiceStatus(
    string ServiceName,
    bool Installed,
    string State,
    string? StartMode,
    bool InstallManifestPresent,
    Guid? InstallId,
    string? ProductVersion,
    string? ServiceAccount,
    string? ServiceSid,
    string? InstallManifestError);

public static class HostServiceStatusReader
{
    public static HostServiceStatus Read(
        string serviceName,
        string manifestPath,
        IWindowsServiceQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(query);

        var snapshot = query.GetStatus(serviceName);
        if (!File.Exists(manifestPath))
        {
            return new HostServiceStatus(
                serviceName,
                snapshot.Installed,
                snapshot.State,
                snapshot.StartMode,
                InstallManifestPresent: false,
                InstallId: null,
                ProductVersion: null,
                ServiceAccount: null,
                ServiceSid: null,
                InstallManifestError: null);
        }

        try
        {
            var manifest = CipherVaultServiceConfiguration.LoadInstallManifest(
                manifestPath);
            return new HostServiceStatus(
                serviceName,
                snapshot.Installed,
                snapshot.State,
                snapshot.StartMode,
                InstallManifestPresent: true,
                manifest.InstallId,
                manifest.ProductVersion,
                manifest.ServiceAccount,
                manifest.ServiceSid,
                InstallManifestError: null);
        }
        catch (VaultException exception)
        {
            return new HostServiceStatus(
                serviceName,
                snapshot.Installed,
                snapshot.State,
                snapshot.StartMode,
                InstallManifestPresent: true,
                InstallId: null,
                ProductVersion: null,
                ServiceAccount: null,
                ServiceSid: null,
                InstallManifestError: exception.Message);
        }
    }
}
