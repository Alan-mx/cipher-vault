using CipherVault.Core.Documents;

namespace CipherVault.Application.Tests;

public sealed class DocumentMapQueryTests
{
    [Fact]
    public async Task MapLinksBacklinksTagsAndMentionsUseCurrentVersion()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var target = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Target",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "# Target\nBody",
                ["Shared"]),
            actor);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "See Target and [[Target]].",
                ["Shared"]),
            actor);

        var targetMap = await fixture.Documents.GetMapAsync(target.DocumentId);
        var sourceMap = await fixture.Documents.GetMapAsync(source.DocumentId);
        var tags = await fixture.Documents.ListTagsAsync(
            new CursorPageRequest());
        var backlinks = await fixture.Documents.ListBacklinksAsync(
            target.DocumentId,
            new CursorPageRequest());
        var mentions = await fixture.Documents.ListUnlinkedMentionsAsync(
            target.DocumentId,
            new CursorPageRequest());

        Assert.Equal(1, targetMap.BacklinkCount);
        Assert.Equal("Target", targetMap.Metadata.Title);
        Assert.Single(sourceMap.OutgoingLinks);
        Assert.Equal("resolved", sourceMap.OutgoingLinks[0].ResolutionStatus);
        Assert.Equal("Target", sourceMap.OutgoingLinks[0].Target!.Title);
        Assert.Single(tags.Items);
        Assert.Equal("Shared", tags.Items[0].Name);
        Assert.Equal(2, tags.Items[0].DocumentCount);
        Assert.Single(backlinks.Items);
        Assert.Equal("Source", backlinks.Items[0].SourceTitle);
        Assert.Single(mentions.Items);
        Assert.Equal(1, mentions.Items[0].OccurrenceCount);
    }
}
