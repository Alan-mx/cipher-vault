using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CipherVault.Core.Documents;

internal static class CursorCodec
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    internal static string Fingerprint(params string?[] values)
    {
        var canonical = string.Join(
            '\u001F',
            values.Select(value => value ?? string.Empty));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static string Encode<TPayload>(
        string kind,
        string fingerprint,
        TPayload payload)
    {
        var envelope = new CursorEnvelope<TPayload>(
            CurrentVersion,
            kind,
            fingerprint,
            payload);
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        return Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static bool TryDecode<TPayload>(
        string? cursor,
        string expectedKind,
        string expectedFingerprint,
        out TPayload? payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var base64 = cursor
                .Replace('-', '+')
                .Replace('_', '/');
            base64 = base64.PadRight(
                base64.Length + ((4 - (base64.Length % 4)) % 4),
                '=');
            var bytes = Convert.FromBase64String(base64);
            var envelope = JsonSerializer.Deserialize<CursorEnvelope<TPayload>>(
                bytes,
                JsonOptions);
            if (envelope is null
                || envelope.Version != CurrentVersion
                || !string.Equals(envelope.Kind, expectedKind, StringComparison.Ordinal)
                || !string.Equals(
                    envelope.Fingerprint,
                    expectedFingerprint,
                    StringComparison.Ordinal))
            {
                return false;
            }

            payload = envelope.Payload;
            return payload is not null;
        }
        catch
        {
            return false;
        }
    }

    private sealed record CursorEnvelope<TPayload>(
        int Version,
        string Kind,
        string Fingerprint,
        TPayload Payload);
}
