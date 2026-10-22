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

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            startInfo.ArgumentList.Add("--manifest");
            startInfo.ArgumentList.Add(Path.GetFullPath(manifestPath));
        }
        return Process.Start(startInfo)
            ?? throw new VaultException(
                VaultErrorCode.StorageError,
                "Auth Broker process could not be started.");
    }
}
