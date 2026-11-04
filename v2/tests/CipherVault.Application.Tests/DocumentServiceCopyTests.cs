using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class DocumentServiceCopyTests
{
    [Fact]
    public async Task CopiesMarkdownWithNewIdentityAndRevisionOne()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                "notes/Source.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Source\n\n[Target](Target.md)",
                ["Tag"]),
            actor);

        var copied = await fixture.Documents.CopyIdempotentAsync(
            source.DocumentId,
            1,
            new CopyDocumentRequest(null, null),
            actor,
            Guid.NewGuid());

        Assert.NotEqual(source.DocumentId, copied.DocumentId);
        Assert.Equal(1, copied.Revision);
        Assert.Equal("notes/Source 副本.md", copied.VirtualPath);
        var metadata = await fixture.Documents.GetAsync(copied.DocumentId);
        Assert.Equal("Source 副本", metadata.Title);
        Assert.Equal(["Tag"], metadata.Tags);
        var content = await fixture.Documents.ReadContentAsync(
            copied.DocumentId,
            0,
            500);
        Assert.Contains(
            $"id: {copied.DocumentId:D}",
            content.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"id: {source.DocumentId:D}",
            content.Content,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "# Source\n\n[Target](Target.md)",
            content.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyingTextPreservesBodyAndGeneratesNewId()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Plain",
                null,
                null,
                VaultDocumentFormat.Text,
                "plain body",
                ["TextTag"]),
            actor);

        var copied = await fixture.Documents.CopyIdempotentAsync(
            source.DocumentId,
            1,
            new CopyDocumentRequest("Plain copy", "copies/Plain.txt"),
            actor,
            Guid.NewGuid());

        var content = await fixture.Documents.ReadContentAsync(
            copied.DocumentId,
            0,
            100);
        Assert.Equal("plain body", content.Content);
        Assert.NotEqual(source.DocumentId, copied.DocumentId);
        Assert.Equal(
            ["TextTag"],
            (await fixture.Documents.GetAsync(copied.DocumentId)).Tags);
    }

    [Fact]
    public async Task CopyIsIdempotentAndExplicitPathConflictIsRejected()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "Body",
                null),
            actor);
        var key = Guid.NewGuid();
        var request = new CopyDocumentRequest("Copy", "copy.md");

        var first = await fixture.Documents.CopyIdempotentAsync(
            source.DocumentId,
            1,
            request,
            actor,
            key);
        var replay = await fixture.Documents.CopyIdempotentAsync(
            source.DocumentId,
            1,
            request,
            actor,
            key);

        Assert.Equal(first, replay);
        var conflict = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.CopyIdempotentAsync(
                source.DocumentId,
                1,
                new CopyDocumentRequest("Another", "copy.md"),
                actor,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.DocumentPathConflict, conflict.Code);
    }

    [Fact]
    public async Task McpCannotCopyDocuments()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                null,
                null,
                VaultDocumentFormat.Text,
                "Body",
                null),
            user);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.CopyIdempotentAsync(
                source.DocumentId,
                1,
                new CopyDocumentRequest(null, null),
                mcp,
                Guid.NewGuid()));

        Assert.Equal(VaultErrorCode.Forbidden, exception.Code);
    }

    [Fact]
    public async Task DefaultCopyTitleStaysWithinTitleLimit()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                new string('a', 200),
                null,
                null,
                VaultDocumentFormat.Text,
                "Body",
                null),
            actor);

        var copied = await fixture.Documents.CopyIdempotentAsync(
            source.DocumentId,
            1,
            new CopyDocumentRequest(null, null),
            actor,
            Guid.NewGuid());

        var metadata = await fixture.Documents.GetAsync(copied.DocumentId);
        Assert.Equal(200, UnicodeText.CountScalars(metadata.Title));
        Assert.EndsWith(" 副本", metadata.Title, StringComparison.Ordinal);
    }
}
