using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace CipherVault.Core.Crypto;

public static class Argon2PasswordDeriver
{
    public const int KekBytes = 32;

    public static byte[] DeriveKek(
        string password,
        VaultKdfParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        PasswordPolicy.ValidateEncoding(password);
        parameters.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = parameters.Salt,
                MemorySize = parameters.MemoryKiB,
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism
            };
            return argon2.GetBytes(KekBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
