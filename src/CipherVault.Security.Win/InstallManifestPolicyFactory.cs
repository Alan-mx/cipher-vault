using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;

namespace CipherVault.Security.Win;

public static class InstallManifestPolicyFactory
{
    public static ProcessVerificationPolicy CreateServerPolicy(
        CipherVaultInstallPublicManifest manifest,
        bool requireSignature = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var images = (manifest.AllowedServerImages ?? [])
            .Select(ToContract)
            .Where(image => image.Kind == VerifiedProcessKind.HostService)
            .ToArray();
        if (images.Length == 0)
        {
            throw new InvalidOperationException(
                "Install manifest does not contain a Host service image.");
        }

        var signerThumbprints = images
            .Select(image => image.SignerThumbprint)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ProcessVerificationPolicy(
            VerifiedProcessKind.HostService,
            manifest.ServiceSid,
            ExpectedLogonSid: null,
            ExpectedSessionId: 0,
            images,
            signerThumbprints,
            RequireSha256: true,
            requireSignature);
    }

    public static VerifiedProcessKind ParseKind(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "host" or "service" => VerifiedProcessKind.HostService,
            "ui" => VerifiedProcessKind.AdminUi,
            "cli" => VerifiedProcessKind.AdminCli,
            "broker" => VerifiedProcessKind.AuthBroker,
            "bridge" => VerifiedProcessKind.McpBridge,
            _ => throw new InvalidOperationException(
                $"Unsupported process image kind: {value}")
        };
    }

    private static ProcessImageIdentity ToContract(
        CipherVaultImagePolicyFile image)
    {
        if (string.IsNullOrWhiteSpace(image.Path) ||
            string.IsNullOrWhiteSpace(image.Sha256))
        {
            throw new InvalidOperationException(
                "Install manifest process image is incomplete.");
        }
        return new ProcessImageIdentity(
            ParseKind(image.Kind),
            image.Path,
            image.Sha256,
            image.SignerThumbprint,
            image.SignerSubject);
    }
}
