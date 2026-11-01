namespace CipherVault.Core.Backup;

public sealed record BackupManifest(
    string Format,
    int FormatVersion,
    Guid VaultId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<BackupManifestFile> Files);

public sealed record BackupManifestFile(
    string Path,
    long Bytes,
    string Sha256);

public sealed record BackupEntryHashes(
    long DatabaseBytes,
    string DatabaseSha256,
    long KeyEnvelopeBytes,
    string KeyEnvelopeSha256);

public enum BackupCredentialKind
{
    SnapshotDatabaseKey,
    InteractivePassword
}

public abstract class BackupVerificationCredential : IDisposable
{
    public abstract BackupCredentialKind Kind { get; }

    public abstract void Dispose();
}

public sealed class SnapshotDatabaseKeyCredential
    : BackupVerificationCredential
{
    private readonly ReadOnlyMemory<byte> databaseKey;

    public SnapshotDatabaseKeyCredential(
        ReadOnlyMemory<byte> databaseKey)
    {
        if (databaseKey.Length != 32)
        {
            throw new ArgumentException(
                "DatabaseKey must contain exactly 32 bytes.",
                nameof(databaseKey));
        }

        this.databaseKey = databaseKey;
    }

    public override BackupCredentialKind Kind =>
        BackupCredentialKind.SnapshotDatabaseKey;

    internal ReadOnlyMemory<byte> DatabaseKey => databaseKey;

    public override void Dispose()
    {
        // The lease that owns the key is disposed by its caller.
    }
}

public sealed class InteractivePasswordCredential
    : BackupVerificationCredential
{
    public InteractivePasswordCredential(string password)
    {
        Password = password
            ?? throw new ArgumentNullException(nameof(password));
    }

    public override BackupCredentialKind Kind =>
        BackupCredentialKind.InteractivePassword;

    internal string Password { get; }

    public override void Dispose()
    {
        // .NET strings are immutable; the credential is never persisted or logged.
    }
}

public sealed record BackupVerificationRequest(
    string PackagePath,
    string WorkingDirectory,
    BackupVerificationCredential Credential,
    bool RequireCurrentSchema);

public sealed record VerifiedBackupCacheEntry(
    RegularFileIdentity Identity,
    Guid VaultId,
    string WholeFileSha256,
    DateTimeOffset VerifiedAtUtc);

public sealed record BackupVerificationResult(
    BackupManifest Manifest,
    string DatabaseSha256,
    string KeyEnvelopeSha256,
    string WholeFileSha256,
    Guid EnvelopeVaultId,
    Guid DatabaseVaultId,
    int SchemaVersion);

public sealed record RestoreVerificationResult(
    Guid VaultId,
    int SchemaVersion,
    int FormatVersion,
    long DatabaseBytes,
    long KeyEnvelopeBytes);
