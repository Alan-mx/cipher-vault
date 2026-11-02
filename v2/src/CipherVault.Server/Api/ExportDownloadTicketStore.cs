using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Api;

internal enum ExportTicketConsumeResult
{
    Success,
    Invalid,
    SessionMismatch
}

internal sealed record ExportDownloadTicketBinding(
    Guid SessionId,
    long SessionGeneration,
    VaultActorKind ActorKind,
    string? CorrelationId);

internal sealed record IssuedExportDownloadTicket(
    string Ticket,
    DateTimeOffset ExpiresAtUtc,
    Guid SessionId,
    long SessionGeneration);

internal sealed class ExportDownloadTicketStore
{
    private static readonly TimeSpan TicketLifetime =
        TimeSpan.FromMinutes(2);
    private const int MaximumTickets = 1024;
    private const int MaximumPerSession = 8;
    private readonly object sync = new();
    private readonly Dictionary<string, TicketEntry> entries =
        new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;

    public ExportDownloadTicketStore(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    internal IssuedExportDownloadTicket? Issue(
        Guid sessionId,
        long sessionGeneration,
        VaultActorContext actor)
    {
        lock (sync)
        {
            CleanupExpiredLocked();
            if (entries.Count >= MaximumTickets
                || entries.Values.Count(entry =>
                    entry.SessionId == sessionId) >= MaximumPerSession)
            {
                return null;
            }

            var random = RandomNumberGenerator.GetBytes(32);
            try
            {
                var ticket = Convert.ToBase64String(random)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
                var now = timeProvider.GetTimestamp();
                var expiresAtUtc = timeProvider.GetUtcNow()
                    + TicketLifetime;
                entries.Add(
                    Hash(ticket),
                    new TicketEntry(
                        sessionId,
                        sessionGeneration,
                        actor.Kind,
                        actor.CorrelationId,
                        now));
                return new IssuedExportDownloadTicket(
                    ticket,
                    expiresAtUtc,
                    sessionId,
                    sessionGeneration);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(random);
            }
        }
    }

    internal ExportTicketConsumeResult TryConsume(
        string? ticket,
        Guid sessionId,
        long sessionGeneration,
        out ExportDownloadTicketBinding binding)
    {
        binding = null!;
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return ExportTicketConsumeResult.Invalid;
        }

        var hash = Hash(ticket);
        lock (sync)
        {
            CleanupExpiredLocked();
            if (!entries.Remove(hash, out var entry))
            {
                return ExportTicketConsumeResult.Invalid;
            }

            if (entry.SessionId != sessionId
                || entry.SessionGeneration != sessionGeneration)
            {
                return ExportTicketConsumeResult.SessionMismatch;
            }

            binding = new ExportDownloadTicketBinding(
                entry.SessionId,
                entry.SessionGeneration,
                entry.ActorKind,
                entry.CorrelationId);
            return ExportTicketConsumeResult.Success;
        }
    }

    private void CleanupExpiredLocked()
    {
        var now = timeProvider.GetTimestamp();
        foreach (var key in entries
                     .Where(pair =>
                         timeProvider.GetElapsedTime(pair.Value.IssuedAtTimestamp)
                         >= TicketLifetime)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            entries.Remove(key);
        }
    }

    private static string Hash(string ticket) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(ticket)))
            .ToLowerInvariant();

    private sealed record TicketEntry(
        Guid SessionId,
        long SessionGeneration,
        VaultActorKind ActorKind,
        string? CorrelationId,
        long IssuedAtTimestamp);
}
