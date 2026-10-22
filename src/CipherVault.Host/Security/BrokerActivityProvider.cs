using CipherVault.Core.Session;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Security;

public sealed class BrokerActivityProvider(
    TimeProvider? timeProvider = null,
    TimeSpan? staleAfter = null) : ISystemActivityProvider
{
    public static readonly TimeSpan DefaultStaleAfter =
        TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _staleAfter = staleAfter ?? DefaultStaleAfter;
    private BrokerActivityReport? _report;
    private DateTimeOffset _reportedAt;

    public void Report(BrokerActivityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.SessionId == 0 ||
            report.IdleMilliseconds < 0)
        {
            throw new ArgumentException(
                "Broker activity report is invalid.",
                nameof(report));
        }
        var idleMilliseconds = Math.Min(
            report.IdleMilliseconds,
            (long)TimeSpan.FromDays(7).TotalMilliseconds);
        lock (_sync)
        {
            _report = report with { IdleMilliseconds = idleMilliseconds };
            _reportedAt = _timeProvider.GetUtcNow();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _report = null;
        }
    }

    public SystemActivitySnapshot GetSnapshot()
    {
        lock (_sync)
        {
            if (_report is null ||
                _timeProvider.GetUtcNow() - _reportedAt > _staleAfter)
            {
                return new SystemActivitySnapshot(
                    TimeSpan.FromDays(1),
                    IsWorkstationLocked: true);
            }
            return new SystemActivitySnapshot(
                TimeSpan.FromMilliseconds(_report.IdleMilliseconds),
                _report.IsWorkstationLocked);
        }
    }
}
