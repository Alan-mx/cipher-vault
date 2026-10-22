using System.Security.Cryptography;
using System.Text;
using CipherVault.Host.Contracts;

namespace CipherVault.Ipc;

public static class PipeHandshakeTranscript
{
    public static string Compute(
        string pipeName,
        string protocolVersion,
        string clientNonceB64Url,
        string serverNonceB64Url,
        VerifiedProcessIdentity client,
        VerifiedProcessIdentity server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientNonceB64Url);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverNonceB64Url);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(server);

        var transcript = string.Join(
            '\n',
            protocolVersion,
            pipeName,
            clientNonceB64Url,
            serverNonceB64Url,
            IdentityValue(client),
            IdentityValue(server));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(transcript));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool FixedTimeEquals(string expected, string presented)
    {
        if (string.IsNullOrWhiteSpace(expected) ||
            string.IsNullOrWhiteSpace(presented))
        {
            return false;
        }
        try
        {
            var expectedBytes = PipeEncoding.DecodeBase64Url(expected);
            var presentedBytes = PipeEncoding.DecodeBase64Url(presented);
            return CryptographicOperations.FixedTimeEquals(
                expectedBytes,
                presentedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string IdentityValue(VerifiedProcessIdentity identity)
    {
        return string.Join(
            '|',
            identity.Kind,
            identity.ProcessId,
            identity.ProcessStartedAt.ToUnixTimeMilliseconds(),
            identity.UserSid,
            identity.LogonSid,
            identity.SessionId,
            identity.FinalImagePath,
            identity.ImageSha256,
            identity.SignerThumbprint ?? string.Empty);
    }
}
