using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class BrokerActivityProviderTests
{
    [Fact]
    public void MissingReportFailsClosed()
    {
        var provider = new BrokerActivityProvider();

        Assert.Equal(
            new SystemActivitySnapshot(TimeSpan.FromDays(1), true),
            provider.GetSnapshot());
    }

    [Fact]
    public void FreshReportUsesBrokerActivity()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        var provider = new BrokerActivityProvider(time);
        provider.Report(new BrokerActivityReport(
            SessionId: 7,
            IdleMilliseconds: 2_500,
            IsWorkstationLocked: false));

        Assert.Equal(
            new SystemActivitySnapshot(
                TimeSpan.FromSeconds(2.5),
                IsWorkstationLocked: false),
            provider.GetSnapshot());
    }

    [Fact]
    public void StaleReportFailsClosed()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        var provider = new BrokerActivityProvider(time);
        provider.Report(new BrokerActivityReport(
            SessionId: 7,
            IdleMilliseconds: 0,
            IsWorkstationLocked: false));
        time.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(
            new SystemActivitySnapshot(TimeSpan.FromDays(1), true),
            provider.GetSnapshot());
    }

    [Fact]
    public void LockReportLocksVault()
    {
        var provider = new BrokerActivityProvider();
        provider.Report(new BrokerActivityReport(
            SessionId: 7,
            IdleMilliseconds: 0,
            IsWorkstationLocked: true));

        Assert.True(provider.GetSnapshot().IsWorkstationLocked);
    }

    [Fact]
    public void ClearedReportFailsClosed()
    {
        var provider = new BrokerActivityProvider();
        provider.Report(new BrokerActivityReport(
            SessionId: 7,
            IdleMilliseconds: 0,
            IsWorkstationLocked: false));
        provider.Clear();

        Assert.Equal(
            new SystemActivitySnapshot(TimeSpan.FromDays(1), true),
            provider.GetSnapshot());
    }

    [Fact]
    public void InvalidReportIsRejected()
    {
        var provider = new BrokerActivityProvider();

        Assert.Throws<ArgumentException>(() =>
            provider.Report(new BrokerActivityReport(0, 0, false)));
        Assert.Throws<ArgumentException>(() =>
            provider.Report(new BrokerActivityReport(1, -1, false)));
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan duration)
        {
            _now += duration;
        }
    }
}
