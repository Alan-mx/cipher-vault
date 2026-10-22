using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Client;

public static class StrictServiceClientPolicyFactory
{
    public static BrokerPipeClientOptions CreateBrokerOptions(
        CipherVaultInstallPublicManifest manifest,
        string pipeName,
        string ownerSid,
        uint ownerSessionId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);
        var serviceSid = manifest.ServiceSid
            ?? throw Invalid("Install manifest service SID is required.");
        var serverPolicy = CreatePolicy(
            VerifiedProcessKind.HostService,
            serviceSid,
            expectedSessionId: 0,
            manifest.AllowedServerImages);
        var clientPolicy = CreatePolicy(
            VerifiedProcessKind.AuthBroker,
            ownerSid,
            expectedSessionId: ownerSessionId,
            manifest.AllowedClientImages);
        return new BrokerPipeClientOptions(
            pipeName,
            clientPolicy,
            serverPolicy);
    }

    private static ProcessVerificationPolicy CreatePolicy(
        VerifiedProcessKind kind,
        string expectedUserSid,
        uint expectedSessionId,
        IReadOnlyList<CipherVaultImagePolicyFile>? configuredImages)
    {
        var images = (configuredImages ?? [])
            .Where(image =>
                Enum.TryParse<VerifiedProcessKind>(
                    image.Kind,
                    ignoreCase: true,
                    out var imageKind) &&
                imageKind == kind)
            .Select(image => new ProcessImageIdentity(
                kind,
                Path.GetFullPath(image.Path!),
                image.Sha256!,
                image.SignerThumbprint,
                image.SignerSubject))
            .ToArray();
        if (images.Length == 0)
        {
            throw Invalid($"Install manifest has no {kind} image policy.");
        }
        var signerThumbprints = images
            .Select(image => image.SignerThumbprint)
            .Where(thumbprint => !string.IsNullOrWhiteSpace(thumbprint))
            .Select(thumbprint => thumbprint!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (signerThumbprints.Length == 0)
        {
            throw Invalid($"Install manifest has no {kind} signer policy.");
        }
        return new ProcessVerificationPolicy(
            kind,
            expectedUserSid,
            ExpectedLogonSid: null,
            expectedSessionId,
            images,
            signerThumbprints,
            RequireSha256: true,
            RequireSignature: true);
    }

    private static VaultException Invalid(string message)
    {
        return new VaultException(
            VaultErrorCode.InvalidInput,
            message);
    }
}
