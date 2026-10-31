using System.Globalization;
using System.Text;

namespace CipherVault.Core.Crypto;

public static class VaultEnvelopeAad
{
    public static byte[] Build(Guid vaultId, VaultKdfParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();

        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"cipher-vault-key-envelope-v1|{vaultId:D}|argon2id|{parameters.MemoryKiB}|{parameters.Iterations}|{parameters.Parallelism}");
        return Encoding.UTF8.GetBytes(value);
    }
}
