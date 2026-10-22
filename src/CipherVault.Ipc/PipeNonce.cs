using System.Security.Cryptography;

namespace CipherVault.Ipc;

public static class PipeNonce
{
    public static string Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        try
        {
            return PipeEncoding.DecodeBase64Url(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
