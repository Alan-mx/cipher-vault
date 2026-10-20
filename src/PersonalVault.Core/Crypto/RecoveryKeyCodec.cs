using System.Security.Cryptography;
using System.Text;
using PersonalVault.Core.Errors;

namespace PersonalVault.Core.Crypto;

public static class RecoveryKeyCodec
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int RecoveryKeyBytes = 32;
    private const int ChecksumBytes = 4;

    public static string Format(byte[] recoveryKey)
    {
        if (recoveryKey.Length != RecoveryKeyBytes)
        {
            throw new ArgumentException("Recovery key must be 32 bytes.", nameof(recoveryKey));
        }

        var checksum = SHA256.HashData(recoveryKey)[..ChecksumBytes];
        var encoded = Encode(recoveryKey) + Encode(checksum);
        return string.Join(
            "-",
            Enumerable.Range(0, (encoded.Length + 3) / 4)
                .Select(index =>
                {
                    var start = index * 4;
                    return encoded.Substring(start, Math.Min(4, encoded.Length - start));
                }));
    }

    public static byte[] Parse(string formatted)
    {
        var compact = new string(
            formatted
                .Where(character => !char.IsWhiteSpace(character) && character != '-')
                .Select(character => char.ToUpperInvariant(character))
                .ToArray());

        var payloadLength = GetEncodedLength(RecoveryKeyBytes);
        var checksumLength = GetEncodedLength(ChecksumBytes);
        if (compact.Length != payloadLength + checksumLength)
        {
            throw new VaultException(VaultErrorCode.InvalidCredential, "Recovery key format is invalid.");
        }

        var payload = Decode(compact[..payloadLength], RecoveryKeyBytes);
        var expectedChecksum = Decode(compact[payloadLength..], ChecksumBytes);
        var actualChecksum = SHA256.HashData(payload)[..ChecksumBytes];
        if (!CryptographicOperations.FixedTimeEquals(expectedChecksum, actualChecksum))
        {
            throw new VaultException(VaultErrorCode.InvalidCredential, "Recovery key checksum is invalid.");
        }

        return payload;
    }

    private static int GetEncodedLength(int byteCount)
    {
        return (byteCount * 8 + 4) / 5;
    }

    private static string Encode(byte[] data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return builder.ToString();
    }

    private static byte[] Decode(string encoded, int expectedLength)
    {
        var output = new byte[expectedLength];
        var buffer = 0;
        var bits = 0;
        var offset = 0;

        foreach (var character in encoded)
        {
            var value = DecodeCharacter(character);
            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                if (offset >= output.Length)
                {
                    throw new VaultException(VaultErrorCode.InvalidCredential, "Recovery key encoding is invalid.");
                }
                output[offset++] = (byte)((buffer >> bits) & 0xFF);
            }
        }

        if (offset != expectedLength || (bits > 0 && (buffer & ((1 << bits) - 1)) != 0))
        {
            throw new VaultException(VaultErrorCode.InvalidCredential, "Recovery key encoding is invalid.");
        }

        return output;
    }

    private static int DecodeCharacter(char character)
    {
        character = character switch
        {
            'I' or 'L' => '1',
            'O' => '0',
            _ => character
        };

        var value = Alphabet.IndexOf(character);
        if (value < 0)
        {
            throw new VaultException(VaultErrorCode.InvalidCredential, "Recovery key contains an invalid character.");
        }
        return value;
    }
}
