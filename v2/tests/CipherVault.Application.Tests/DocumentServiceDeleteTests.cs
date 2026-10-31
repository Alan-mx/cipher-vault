using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class DocumentServiceDeleteTests
{
    [Fact]
    public async Task SoftDeleteHidesDocumentButRetainsDataAndAudit()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var target = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Target",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "# Target",
                null),
            actor);
        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "[[Target]]",
                null),
            actor);

        var deleted = await fixture.Documents.DeleteAsync(
            target.DocumentId,
            1,
            actor);

        Assert.Equal(2, deleted.Revision);
        Assert.Equal(
            30,
            (deleted.PurgeEligibleAt - deleted.DeletedAt).TotalDays,
            precision: 6);
        await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.GetAsync(target.DocumentId));
        var counts = await fixture.QueryAsync(
            async context =>
            {
                using var lease = context.CreateCommand();
                lease.Command.CommandText =
                    """
                    SELECT
                      (SELECT count(*) FROM documents WHERE deleted_at IS NOT NULL),
                      (SELECT count(*) FROM document_fts),
                      (SELECT resolution_status FROM document_links LIMIT 1),
                      (SELECT count(*) FROM document_revisions WHERE document_id = $id);
                    """;
                lease.Command.Parameters.AddWithValue("$id", target.DocumentId.ToString("D"));
                await using var reader = await lease.Command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                return (
                    Trash: reader.GetInt32(0),
                    Fts: reader.GetInt32(1),
                    LinkStatus: reader.GetString(2),
                    Revisions: reader.GetInt32(3));
            });

        Assert.Equal(1, counts.Trash);
        Assert.Equal(1, counts.Fts);
        Assert.Equal("unresolved", counts.LinkStatus);
        Assert.Equal(2, counts.Revisions);

        var conflict = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.DeleteAsync(target.DocumentId, 1, actor));
        Assert.Equal(VaultErrorCode.DocumentNotFound, conflict.Code);
    }
}
