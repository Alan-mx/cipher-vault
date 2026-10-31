using System.Security.Cryptography;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Crypto;

public sealed class VaultKeyMaterial : IDisposable, IAsyncDisposable
{
    private readonly byte[] masterKey;
    private readonly byte[] databaseKey;
    private bool disposed;

    internal VaultKeyMaterial(Guid vaultId, byte[] masterKey, byte[] databaseKey)
    {
        if (masterKey.Length != MasterKeyProtector.MasterKeyBytes
            || databaseKey.Length != MasterKeyProtector.MasterKeyBytes)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Vault key material must contain two 32-byte keys.");
        }

        VaultId = vaultId;
        this.masterKey = masterKey;
        this.databaseKey = databaseKey;
    }

    public Guid VaultId { get; }

    public ReadOnlyMemory<byte> MasterKey
    {
        get
        {
            ThrowIfDisposed();
            return masterKey;
        }
    }

    public ReadOnlyMemory<byte> DatabaseKey
    {
        get
        {
            ThrowIfDisposed();
            return databaseKey;
        }
    }

    internal bool IsClearedForTest =>
        masterKey.All(value => value == 0)
        && databaseKey.All(value => value == 0);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(masterKey);
        CryptographicOperations.ZeroMemory(databaseKey);
        disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(VaultKeyMaterial));
        }
    }
}
