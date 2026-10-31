using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class DocumentServiceUpdateTests
{
    [Fact]
    public async Task UpdateChecksRevisionUpdatesIndicesAndNoOpDoesNotAdvance()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Note",
                "Old.md",
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: [Old]\n---\nold",
                null),
            actor);

        var updated = await fixture.Documents.UpdateAsync(
            created.DocumentId,
            1,
            new UpdateDocumentRequest(
                "New",
                "New.md",
                "---\ntags: [New]\n---\nnew",
                ["New"]),
            actor);

        Assert.Equal(2, updated.Revision);
        Assert.Equal("New.md", updated.VirtualPath);
        var map = await fixture.Documents.GetMapAsync(created.DocumentId);
        Assert.Equal("New", map.Metadata.Title);
        Assert.Equal(["New"], map.Metadata.Tags);

        var noOp = await fixture.Documents.UpdateAsync(
            created.DocumentId,
            2,
            new UpdateDocumentRequest(
                "New",
                "New.md",
                "---\ntags: [New]\n---\nnew",
                ["New"]),
            actor);
        Assert.Equal(2, noOp.Revision);
        Assert.Null(noOp.AuditEventId);

        var conflict = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.UpdateAsync(
                created.DocumentId,
                1,
                new UpdateDocumentRequest(
                    "Again",
                    "Again.md",
                    "again",
                    []),
                actor));
        Assert.Equal(VaultErrorCode.RevisionConflict, conflict.Code);
        Assert.Equal(2, conflict.Details["currentRevision"]);
    }

    [Fact]
    public async Task TitleRenameWithoutRewriteLeavesBacklinkUnresolved()
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
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "[[Target]]",
                null),
            actor);

        await fixture.Documents.UpdateAsync(
            target.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Renamed",
                "Renamed.md",
                "# Renamed",
                [],
                UpdateBacklinks: false),
            actor);

        var backlinks = await fixture.Documents.ListBacklinksAsync(
            target.DocumentId,
            new CursorPageRequest());
        Assert.Empty(backlinks.Items);
    }

    [Fact]
    public async Task ExplicitBacklinkRewriteCreatesSourceRevision()
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
                "[[Target|display]]",
                null),
            actor);

        await fixture.Documents.UpdateAsync(
            target.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Renamed",
                null,
                "# Renamed",
                [],
                UpdateBacklinks: true),
            actor);

        var sourceMap = await fixture.Documents.GetMapAsync(source.DocumentId);
        Assert.Equal(2, sourceMap.Metadata.Revision);
        var chunk = await fixture.Documents.ReadContentAsync(source.DocumentId, 0, 100);
        Assert.Contains("[[Renamed|display]]", chunk.Content, StringComparison.Ordinal);
        var backlinks = await fixture.Documents.ListBacklinksAsync(
            target.DocumentId,
            new CursorPageRequest());
        Assert.Single(backlinks.Items);
    }

    [Fact]
    public async Task ConcurrentUpdatesWithSameRevisionHaveSingleWinner()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Concurrent",
                null,
                null,
                VaultDocumentFormat.Text,
                "initial",
                null),
            actor);

        var updates = new[]
        {
            fixture.Documents.UpdateAsync(
                created.DocumentId,
                1,
                new UpdateDocumentRequest(
                    "Concurrent",
                    null,
                    "first",
                    []),
                actor),
            fixture.Documents.UpdateAsync(
                created.DocumentId,
                1,
                new UpdateDocumentRequest(
                    "Concurrent",
                    null,
                    "second",
                    []),
                actor)
        };
        var outcomes = await Task.WhenAll(updates.Select(CaptureOutcomeAsync));

        Assert.Single(outcomes, code => code is null);
        Assert.Single(
            outcomes,
            code => code == VaultErrorCode.RevisionConflict);
        var metadata = await fixture.Documents.GetAsync(created.DocumentId);
        Assert.Equal(2, metadata.Revision);
    }

    private static async Task<VaultErrorCode?> CaptureOutcomeAsync(
        Task<DocumentWriteResult> task)
    {
        try
        {
            _ = await task;
            return null;
        }
        catch (VaultException exception)
        {
            return exception.Code;
        }
    }
}
