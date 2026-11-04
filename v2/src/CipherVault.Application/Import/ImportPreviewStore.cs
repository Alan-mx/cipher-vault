using System.Security.Cryptography;

namespace CipherVault.Application.Import;

internal sealed class ImportPreviewStore
{
    internal const int MaxPendingPreviews = 16;
    private readonly Dictionary<string, PreviewEntry> entries =
        new(StringComparer.Ordinal);
    private readonly object sync = new();

    internal PreviewReservation Reserve(DateTimeOffset now)
    {
        lock (sync)
        {
            RemoveExpired(now);
            if (entries.Count >= MaxPendingPreviews)
            {
                throw new InvalidOperationException(
                    "The import preview capacity is exhausted.");
            }

            string token;
            do
            {
                token = CreateToken();
            }
            while (entries.ContainsKey(token));

            var expiresAt = now.AddMinutes(10);
            entries[token] = new PreviewEntry(expiresAt);
            return new PreviewReservation(token, expiresAt);
        }
    }

    internal void Complete(
        string token,
        string sourceRoot,
        Guid vaultSessionId,
        DateTimeOffset expiresAt,
        string? manifestSha256,
        IReadOnlyList<ImportPreviewItem> items)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(token, out var entry))
            {
                return;
            }

            entry.Preview = new PendingImportPreview(
                sourceRoot,
                vaultSessionId,
                expiresAt,
                manifestSha256,
                items);
        }
    }

    internal void Remove(string token)
    {
        lock (sync)
        {
            entries.Remove(token);
        }
    }

    internal bool TryConsume(
        string token,
        DateTimeOffset now,
        out PendingImportPreview? preview)
    {
        lock (sync)
        {
            RemoveExpired(now);
            if (!entries.Remove(token, out var entry)
                || entry.Preview is null)
            {
                preview = null;
                return false;
            }

            preview = entry.Preview;
            return true;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var token in entries
                     .Where(pair => now >= pair.Value.ExpiresAt)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            entries.Remove(token);
        }
    }

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal sealed record PreviewReservation(
        string Token,
        DateTimeOffset ExpiresAt);

    internal sealed record PendingImportPreview(
        string SourceRoot,
        Guid VaultSessionId,
        DateTimeOffset ExpiresAt,
        string? ManifestSha256,
        IReadOnlyList<ImportPreviewItem> Items);

    private sealed class PreviewEntry
    {
        internal PreviewEntry(DateTimeOffset expiresAt)
        {
            ExpiresAt = expiresAt;
        }

        internal DateTimeOffset ExpiresAt { get; }

        internal PendingImportPreview? Preview { get; set; }
    }
}
