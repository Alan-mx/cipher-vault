using CipherVault.Core.Documents;

namespace CipherVault.Application.Tests;

public sealed class DocumentLinkRebuildTests
{
    [Fact]
    public async Task RebuildRepairsLinkIndexWithoutChangingRevision()
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
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "[[Target]]",
                null),
            actor);
        await fixture.QueryAsync(
            async context =>
            {
                using var lease = context.CreateCommand();
                lease.Command.CommandText =
                    """
                    UPDATE document_links
                    SET target_document_id = NULL,
                        resolution_status = 'unresolved';
                    """;
                return await lease.Command.ExecuteNonQueryAsync();
            });

        var rebuilt = await fixture.Documents.RebuildLinkIndexAsync(actor);

        Assert.Equal(2, rebuilt.DocumentsIndexed);
        Assert.Equal(1, rebuilt.LinksIndexed);
        Assert.NotNull(rebuilt.AuditEventId);
        var sourceAfter = await fixture.Documents.GetAsync(source.DocumentId);
        var targetAfter = await fixture.Documents.GetAsync(target.DocumentId);
        Assert.Equal(1, sourceAfter.Revision);
        Assert.Equal(1, targetAfter.Revision);
        var backlinks = await fixture.Documents.ListBacklinksAsync(
            target.DocumentId,
            new CursorPageRequest());
        Assert.Single(backlinks.Items);
    }

    [Fact]
    public async Task RebuildIncludesWikiAndMarkdownLinks()
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
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "[[Target]] [Target](Target.md)",
                null),
            actor);
        await fixture.QueryAsync(
            async context =>
            {
                using var lease = context.CreateCommand();
                lease.Command.CommandText =
                    """
                    UPDATE document_links
                    SET target_document_id = NULL,
                        resolution_status = 'unresolved';
                    """;
                return await lease.Command.ExecuteNonQueryAsync();
            });

        var rebuilt = await fixture.Documents.RebuildLinkIndexAsync(actor);

        Assert.Equal(2, rebuilt.LinksIndexed);
        var map = await fixture.Documents.GetMapAsync(source.DocumentId);
        Assert.Equal(2, map.OutgoingLinks.Count);
        Assert.Contains(map.OutgoingLinks, link =>
            link.LinkKind == DocumentLinkKind.Wiki
            && link.ResolutionStatus == "resolved");
        Assert.Contains(map.OutgoingLinks, link =>
            link.LinkKind == DocumentLinkKind.Markdown
            && link.ResolutionStatus == "resolved");
        Assert.Single(
            (await fixture.Documents.ListBacklinksAsync(
                target.DocumentId,
                new CursorPageRequest())).Items);
    }
}
