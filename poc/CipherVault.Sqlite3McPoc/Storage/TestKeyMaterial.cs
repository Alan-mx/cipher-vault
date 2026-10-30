using System.Security.Cryptography;

namespace CipherVault.Phase0;

public sealed class TestKeyMaterial : IDisposable
{
    private TestKeyMaterial(byte[] databaseKey)
    {
        DatabaseKey = databaseKey;
        TestKeyId = Convert.ToHexString(SHA256.HashData(databaseKey)).ToLowerInvariant()[..16];
    }

    public byte[] DatabaseKey { get; }

    public string TestKeyId { get; }

    public static async Task<TestKeyMaterial> CreateAsync(string keyPath, CancellationToken cancellationToken)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        await using (var stream = new FileStream(
            keyPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(key, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        return new TestKeyMaterial(key);
    }

    public static async Task<TestKeyMaterial> LoadAsync(string keyPath, CancellationToken cancellationToken)
    {
        var key = await File.ReadAllBytesAsync(keyPath, cancellationToken);
        if (key.Length != 32)
        {
            throw new InvalidDataException("The test-only key must be exactly 32 bytes.");
        }

        return new TestKeyMaterial(key);
    }

    public void Clear() => CryptographicOperations.ZeroMemory(DatabaseKey);

    public void Dispose() => Clear();
}
