using CipherVault.Core.Errors;

namespace CipherVault.Core.Crypto;

public sealed record VaultKdfParameters(
    string Algorithm,
    int MemoryKiB,
    int Iterations,
    int Parallelism,
    byte[] Salt)
{
    public const int DefaultMemoryKiB = 64 * 1024;
    public const int DefaultIterations = 3;
    public const int DefaultParallelism = 1;
    public const int MinMemoryKiB = 16 * 1024;
    public const int MaxMemoryKiB = 256 * 1024;
    public const int MinIterations = 1;
    public const int MaxIterations = 10;
    public const int MinParallelism = 1;
    public const int MaxParallelism = 4;
    public const int SaltBytes = 16;

    public static VaultKdfParameters CreateDefault(ReadOnlySpan<byte> salt)
    {
        if (salt.Length != SaltBytes)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Argon2id Salt must be exactly {SaltBytes} bytes.");
        }

        return new VaultKdfParameters(
            "argon2id",
            DefaultMemoryKiB,
            DefaultIterations,
            DefaultParallelism,
            salt.ToArray());
    }

    public void Validate()
    {
        if (!string.Equals(Algorithm, "argon2id", StringComparison.Ordinal))
        {
            throw Corrupt("Unsupported KDF algorithm.");
        }

        if (MemoryKiB is < MinMemoryKiB or > MaxMemoryKiB)
        {
            throw Corrupt("Argon2id memory is outside the supported range.");
        }

        if (Iterations is < MinIterations or > MaxIterations)
        {
            throw Corrupt("Argon2id iteration count is outside the supported range.");
        }

        if (Parallelism is < MinParallelism or > MaxParallelism)
        {
            throw Corrupt("Argon2id parallelism is outside the supported range.");
        }

        if (Salt is null || Salt.Length != SaltBytes)
        {
            throw Corrupt($"Argon2id Salt must be exactly {SaltBytes} bytes.");
        }
    }

    private static VaultException Corrupt(string message) =>
        new(VaultErrorCode.CorruptVault, message);
}
