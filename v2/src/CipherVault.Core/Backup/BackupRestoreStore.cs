namespace CipherVault.Core.Backup;

public static class BackupRestoreStore
{
    public static Task<RestoreVerificationResult> ExtractAndValidateAsync(
        string packagePath,
        string stagingDirectory,
        BackupVerificationCredential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return ExtractAndValidateCoreAsync(
            packagePath,
            stagingDirectory,
            credential,
            cancellationToken);
    }

    private static async Task<RestoreVerificationResult>
        ExtractAndValidateCoreAsync(
            string packagePath,
            string stagingDirectory,
            BackupVerificationCredential credential,
            CancellationToken cancellationToken)
    {
        using (credential)
        {
            var verified = await BackupPackageVerifier.VerifyAsync(
                new BackupVerificationRequest(
                    packagePath,
                    stagingDirectory,
                    credential),
                cancellationToken);
            return new RestoreVerificationResult(
                verified.Manifest.VaultId,
                verified.SchemaVersion,
                verified.Manifest.FormatVersion,
                verified.Manifest.Files[0].Bytes,
                verified.Manifest.Files[1].Bytes);
        }
    }
}
