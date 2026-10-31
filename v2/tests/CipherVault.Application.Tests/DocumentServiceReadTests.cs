using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class DocumentServiceReadTests
{
    [Fact]
    public async Task MetadataAndContentChunkPreserveUnicodeBoundary()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Unicode",
                null,
                null,
                VaultDocumentFormat.Text,
                "A😀B",
                ["Tag"]),
            actor);

        var metadata = await fixture.Documents.GetAsync(created.DocumentId);
        var chunk = await fixture.Documents.ReadContentAsync(
            created.DocumentId,
            0,
            2);

        Assert.Equal("Unicode", metadata.Title);
        Assert.Equal(["Tag"], metadata.Tags);
        Assert.Equal("A😀", chunk.Content);
        Assert.Equal(3, chunk.NextOffset);
        Assert.False(chunk.Done);
        var final = await fixture.Documents.ReadContentAsync(
            created.DocumentId,
            chunk.NextOffset,
            2);
        Assert.Equal("B", final.Content);
        Assert.True(final.Done);
        Assert.Equal(4, final.TotalLength);
    }

    [Fact]
    public async Task CatalogIsCaseSensitiveAndCursorBound()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "A",
                "folder/a.md",
                null,
                VaultDocumentFormat.Markdown,
                "a",
                null),
            actor);
        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "B",
                "folder/b.md",
                null,
                VaultDocumentFormat.Markdown,
                "b",
                null),
            actor);
        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "C",
                "Folder/c.md",
                null,
                VaultDocumentFormat.Markdown,
                "c",
                null),
            actor);

        var first = await fixture.Documents.ListAsync(
            new CatalogQuery(
                "folder",
                null,
                new CursorPageRequest(1)));
        Assert.Single(first.Items);
        Assert.Equal("folder/a.md", first.Items[0].VirtualPath);
        Assert.NotNull(first.NextCursor);

        var second = await fixture.Documents.ListAsync(
            new CatalogQuery(
                "folder",
                null,
                new CursorPageRequest(1, first.NextCursor)));
        Assert.Single(second.Items);
        Assert.Equal("folder/b.md", second.Items[0].VirtualPath);
        Assert.Null(second.NextCursor);

        var invalid = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.ListAsync(
                new CatalogQuery(
                    "other",
                    null,
                    new CursorPageRequest(1, first.NextCursor))));
        Assert.Equal(VaultErrorCode.InvalidCursor, invalid.Code);
    }
}
