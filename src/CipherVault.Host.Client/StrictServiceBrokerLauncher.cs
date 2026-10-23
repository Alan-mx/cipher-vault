using System.Diagnostics;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Host.Contracts;
using CipherVault.Security.Win;

namespace CipherVault.Host.Client;

public static class StrictServiceBrokerLauncher
{
    public static Process EnsureStarted(
        CipherVaultInstallPublicManifest manifest,
        string? manifestPath = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var image = (manifest.AllowedClientImages ?? [])
            .Where(image =>
                Enum.TryParse<VerifiedProcessKind>(
                    image.Kind,
                    ignoreCase: true,
                    out var kind) &&
                kind == VerifiedProcessKind.AuthBroker)
            .SingleOrDefault()
            ?? throw new VaultException(
                VaultErrorCode.NotFound,
                "Install manifest does not contain an Auth Broker image.");
        var executablePath = Path.GetFullPath(image.Path!);
        if (!File.Exists(executablePath))
        {
            throw new VaultException(
                VaultErrorCode.NotFound,
                $"Auth Broker executable was not found: {executablePath}");
        }
        var actualHash = WindowsProcessIdentityVerifier.ComputeFileSha256(
            executablePath);
        if (!string.Equals(
                actualHash,
                image.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Auth Broker executable hash does not match the install manifest.");
        }
        if (!WindowsProcessIdentityVerifier.TryGetAuthenticodeSigner(
                executablePath,
                out var signerThumbprint,
                out _) ||
            !string.Equals(
                signerThumbprint,
                image.SignerThumbprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Auth Broker executable signature does not match the install manifest.");
        }

        var effectiveManifestPath = string.IsNullOrWhiteSpace(manifestPath)
            ? CipherVaultServicePaths.Default.PublicManifestPath
            : Path.GetFullPath(manifestPath);
        var reusable = FindReusableBroker(
            executablePath,
            effectiveManifestPath);
        if (reusable is not null)
        {
            return reusable;
        }

        var startInfo = CreateStartInfo(
            executablePath,
            manifestPath);
        return Process.Start(startInfo)
            ?? throw new VaultException(
                VaultErrorCode.StorageError,
                "Auth Broker process could not be started.");
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string? manifestPath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            startInfo.ArgumentList.Add("--manifest");
            startInfo.ArgumentList.Add(Path.GetFullPath(manifestPath));
        }
        return startInfo;
    }

    private static Process? FindReusableBroker(
        string executablePath,
        string manifestPath)
    {
        var normalizedPath = Path.GetFullPath(executablePath);
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var manifestTimestamp = File.Exists(manifestPath)
            ? File.GetLastWriteTimeUtc(manifestPath)
            : DateTime.MinValue;
        Process? reusable = null;
        DateTime reusableStartedAt = DateTime.MinValue;
        foreach (var candidate in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(normalizedPath)))
        {
            try
            {
                var modulePath = candidate.MainModule?.FileName;
                if (candidate.SessionId != currentSessionId ||
                    string.IsNullOrWhiteSpace(modulePath) ||
                    !string.Equals(
                        Path.GetFullPath(modulePath),
                        normalizedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    candidate.Dispose();
                    continue;
                }

                var startedAt = candidate.StartTime.ToUniversalTime();
                if (startedAt < manifestTimestamp)
                {
                    TryTerminate(candidate);
                    continue;
                }

                if (reusable is null || startedAt > reusableStartedAt)
                {
                    reusable?.Dispose();
                    reusable = candidate;
                    reusableStartedAt = startedAt;
                }
                else
                {
                    TryTerminate(candidate);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException or
                UnauthorizedAccessException)
            {
                candidate.Dispose();
            }
        }
        return reusable;
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            process.Kill();
            _ = process.WaitForExit(5_000);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException or
            UnauthorizedAccessException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
