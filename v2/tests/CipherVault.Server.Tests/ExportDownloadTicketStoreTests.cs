using CipherVault.Core.Documents;
using CipherVault.Server.Api;

namespace CipherVault.Server.Tests;

public sealed class ExportDownloadTicketStoreTests
{
    [Fact]
    public void TicketIsSingleUseAndBoundToSession()
    {
        var time = new ManualTimeProvider();
        var store = new ExportDownloadTicketStore(time);
        var sessionId = Guid.NewGuid();
        var actor = new VaultActorContext(
            VaultActorKind.UserUi,
            "correlation");
        var issued = Assert.IsType<IssuedExportDownloadTicket>(
            store.Issue(sessionId, 4, actor));

        Assert.Equal(
            ExportTicketConsumeResult.SessionMismatch,
            store.TryConsume(
                issued.Ticket,
                Guid.NewGuid(),
                4,
                out _));
        Assert.Equal(
            ExportTicketConsumeResult.Invalid,
            store.TryConsume(issued.Ticket, sessionId, 4, out _));

        var second = Assert.IsType<IssuedExportDownloadTicket>(
            store.Issue(sessionId, 4, actor));
        Assert.Equal(
            ExportTicketConsumeResult.Success,
            store.TryConsume(
                second.Ticket,
                sessionId,
                4,
                out var binding));
        Assert.Equal(actor.Kind, binding.ActorKind);
        Assert.Equal(actor.CorrelationId, binding.CorrelationId);
        Assert.Equal(
            ExportTicketConsumeResult.Invalid,
            store.TryConsume(second.Ticket, sessionId, 4, out _));
    }

    [Fact]
    public void TicketExpiresUsingMonotonicClock()
    {
        var time = new ManualTimeProvider();
        var store = new ExportDownloadTicketStore(time);
        var sessionId = Guid.NewGuid();
        var issued = Assert.IsType<IssuedExportDownloadTicket>(
            store.Issue(
                sessionId,
                1,
                new VaultActorContext(VaultActorKind.UserUi)));

        time.MoveWallClock(TimeSpan.FromHours(-1));
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(
            ExportTicketConsumeResult.Invalid,
            store.TryConsume(issued.Ticket, sessionId, 1, out _));
    }
}
