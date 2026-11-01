using System.Security.Cryptography;
using CipherVault.Core.Backup;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Backup;

public sealed record BackupSnapshotCapture(
    Guid VaultId,
    DateTimeOffset CapturedAtUtc,
    string WorkingDirectory,
    string DatabasePath,
    string KeyEnvelopePath,
    long DatabaseBytes,
    long KeyEnvelopeBytes,
    SensitiveDatabaseKeyLease DatabaseKeyLease);

public sealed record BackupCreateResult(
    BackupListItem Item,
    bool AuditRecorded);

public sealed class SensitiveDatabaseKeyLease : IDisposable
{
    private readonly byte[] databaseKey;
    private int disposed;

    public SensitiveDatabaseKeyLease(ReadOnlySpan<byte> databaseKey)
    {
        if (databaseKey.Length != 32)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "DatabaseKey lease must contain exactly 32 bytes.");
        }

        this.databaseKey = databaseKey.ToArray();
    }

    public ReadOnlyMemory<byte> DatabaseKey
    {
        get
        {
            ThrowIfDisposed();
            return databaseKey;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(databaseKey);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
    }
}
