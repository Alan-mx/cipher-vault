using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Documents;

internal static class CursorCodec
{
    private const int CurrentVersion = 2;

    public static string Encode<T>(
        Guid sessionId,
        string kind,
        string queryFingerprint,
        T position)
    {
        var payload = new CursorPayload
        {
            Version = CurrentVersion,
            SessionId = sessionId,
            Kind = kind,
            QueryFingerprint = queryFingerprint,
            Position = JsonSerializer.SerializeToElement(position)
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode<T>(
        string? cursor,
        Guid sessionId,
        string kind,
        string queryFingerprint,
        out T position)
    {
        position = default!;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var payload = DecodePayload(cursor);
            if (payload.Version != CurrentVersion ||
                payload.SessionId != sessionId)
            {
                throw new VaultException(
                    VaultErrorCode.SessionExpired,
                    "Pagination cursor is no longer valid.");
            }
            if (!string.Equals(payload.Kind, kind, StringComparison.Ordinal) ||
                !string.Equals(
                    payload.QueryFingerprint,
                    queryFingerprint,
                    StringComparison.Ordinal))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Pagination cursor does not match this query.");
            }

            position = payload.Position.Deserialize<T>()
                ?? throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Pagination cursor position is invalid.");
            return true;
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

    private static CursorPayload DecodePayload(string cursor)
    {
        var normalized = cursor.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + ((4 - normalized.Length % 4) % 4),
            '=');
        return JsonSerializer.Deserialize<CursorPayload>(
            Convert.FromBase64String(normalized))
            ?? throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Pagination cursor is empty.");
    }

    private sealed record CursorPayload
    {
        public int Version { get; init; }
        public Guid SessionId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string QueryFingerprint { get; init; } = string.Empty;
        public JsonElement Position { get; init; }
    }
}
