using System.Text;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Crypto;

public static class PasswordPolicy
{
    public const int MinimumUnicodeScalars = 12;

    public static void Validate(string password)
    {
        if (!TryCountUnicodeScalars(password, out var count)
            || count < MinimumUnicodeScalars)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Password must contain at least {MinimumUnicodeScalars} Unicode characters.");
        }
    }

    public static void ValidateAuthenticationAttempt(string? password)
    {
        if (!TryCountUnicodeScalars(password, out _))
        {
            throw new VaultException(
                VaultErrorCode.InvalidCredential,
                "Vault credential is invalid.");
        }
    }

    public static void ValidateEncoding(string? password)
    {
        if (!TryCountUnicodeScalars(password, out _))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Password contains invalid Unicode data.");
        }
    }

    private static bool TryCountUnicodeScalars(string? value, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }

            count++;
        }

        return true;
    }
}
