using CipherVault.Core.Audit;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class AuditQueryServiceTests
{
    [Fact]
    public async Task QueryFiltersAndCursorAreStable()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var first = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "First",
                null,
                null,
                VaultDocumentFormat.Text,
                "one",
                null),
            user);
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Second",
                null,
                null,
                VaultDocumentFormat.Text,
                "two",
                null),
            user);

        var firstPage = await fixture.Audit.ListAsync(
            new AuditQuery(
                VaultActorKind.UserUi,
                "create",
                null,
                null,
                null,
                new CursorPageRequest(1)));
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await fixture.Audit.ListAsync(
            new AuditQuery(
                VaultActorKind.UserUi,
                "create",
                null,
                null,
                null,
                new CursorPageRequest(1, firstPage.NextCursor)));
        Assert.Single(secondPage.Items);
        Assert.NotEqual(firstPage.Items[0].EventId, secondPage.Items[0].EventId);

        var byDocument = await fixture.Audit.ListAsync(
            new AuditQuery(
                null,
                null,
                first.DocumentId,
                null,
                null,
                new CursorPageRequest()));
        Assert.Single(byDocument.Items);

        var systemOnly = await fixture.Audit.ListAsync(
            new AuditQuery(
                VaultActorKind.System,
                null,
                null,
                null,
                null,
                new CursorPageRequest()));
        Assert.Empty(systemOnly.Items);

        var mismatched = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Audit.ListAsync(
                new AuditQuery(
                    VaultActorKind.UserUi,
                    "update",
                    null,
                    null,
                    null,
                    new CursorPageRequest(1, firstPage.NextCursor))));
        Assert.Equal(VaultErrorCode.InvalidCursor, mismatched.Code);
    }

    [Fact]
    public async Task TimeRangeIsInclusiveAndInvalidRangeIsRejected()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Range",
                null,
                null,
                VaultDocumentFormat.Text,
                "body",
                null),
            new VaultActorContext(VaultActorKind.UserUi));

        var now = fixture.TimeProvider.GetUtcNow();
        var page = await fixture.Audit.ListAsync(
            new AuditQuery(
                null,
                null,
                null,
                now,
                now,
                new CursorPageRequest()));
        Assert.Single(page.Items);

        var invalid = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Audit.ListAsync(
                new AuditQuery(
                    null,
                    null,
                    null,
                    now,
                    now.AddTicks(-1),
                    new CursorPageRequest())));
        Assert.Equal(VaultErrorCode.InvalidInput, invalid.Code);
    }
}
