using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalVault.Core.Errors;

namespace PersonalVault.Core.Documents;

internal static class CursorCodec
{
    public static string Encode(Guid sessionId, string kind, int offset, string queryFingerprint)
    {
        var payload = new CursorPayload
        {
            Version = 1,
            SessionId = sessionId,
            Kind = kind,
            Offset = offset,
            QueryFingerprint = queryFingerprint
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static int Decode(
        string? cursor,
        Guid sessionId,
        string kind,
        string queryFingerprint)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + ((4 - normalized.Length % 4) % 4),
                '=');
            var payload = JsonSerializer.Deserialize<CursorPayload>(
                Convert.FromBase64String(normalized));
            if (payload is null ||
                payload.Version != 1 ||
                payload.SessionId != sessionId)
            {
                throw new VaultException(VaultErrorCode.SessionExpired, "Pagination cursor is no longer valid.");
            }
            if (!string.Equals(payload.Kind, kind, StringComparison.Ordinal) ||
                !string.Equals(
                    payload.QueryFingerprint,
                    queryFingerprint,
                    StringComparison.Ordinal) ||
                payload.Offset < 0)
            {
                throw new VaultException(VaultErrorCode.InvalidInput, "Pagination cursor does not match this query.");
            }
            return payload.Offset;
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Pagination cursor is invalid.",
                exception);
        }
    }

    public static string Fingerprint(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed record CursorPayload
    {
        public int Version { get; init; }
        public Guid SessionId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public int Offset { get; init; }
        public string QueryFingerprint { get; init; } = string.Empty;
    }
}
