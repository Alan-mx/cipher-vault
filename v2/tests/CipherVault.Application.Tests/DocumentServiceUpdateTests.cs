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
    public async Task TitleAndPathRenameAutomaticallyRewritesResolvedWikiLink()
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

        await fixture.Documents.UpdateAsync(
            target.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Renamed",
                "Renamed.md",
                "# Renamed",
                []),
            actor);

        var sourceMap = await fixture.Documents.GetMapAsync(source.DocumentId);
        Assert.Equal(2, sourceMap.Metadata.Revision);
        var chunk = await fixture.Documents.ReadContentAsync(
            source.DocumentId,
            0,
            100);
        Assert.Equal("[[Renamed]]", chunk.Content);
        var backlinks = await fixture.Documents.ListBacklinksAsync(
            target.DocumentId,
            new CursorPageRequest());
        Assert.Single(backlinks.Items);
    }

    [Fact]
    public async Task DisplayTextAndHeadingArePreservedWhenWikiLinkIsRewritten()
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
                []),
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
    public async Task TargetMoveRewritesMarkdownDestinationAndPreservesTitle()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var target = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Target",
                "notes/Target.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Top",
                null),
            actor);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                "docs/Source.md",
                null,
                VaultDocumentFormat.Markdown,
                "[Read](../notes/Target.md#Top \"title\")",
                null),
            actor);

        await fixture.Documents.UpdateAsync(
            target.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Target",
                "archive/Target.md",
                "# Top",
                []),
            actor);

        var sourceMap = await fixture.Documents.GetMapAsync(source.DocumentId);
        Assert.Equal(2, sourceMap.Metadata.Revision);
        var chunk = await fixture.Documents.ReadContentAsync(source.DocumentId, 0, 200);
        Assert.Equal(
            "[Read](../archive/Target.md#Top \"title\")",
            chunk.Content);
        var backlinks = await fixture.Documents.ListBacklinksAsync(
            target.DocumentId,
            new CursorPageRequest());
        Assert.Single(backlinks.Items);
    }

    [Fact]
    public async Task SourceMoveRecomputesItsOutgoingMarkdownDestinations()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Target",
                "shared/Target.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Target",
                null),
            actor);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                "notes/projects/Source.md",
                null,
                VaultDocumentFormat.Markdown,
                "[Read](../../shared/Target.md)",
                null),
            actor);

        await fixture.Documents.UpdateAsync(
            source.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Source",
                "shared/Source.md",
                "Edited intro\n\n[Read](../../shared/Target.md)",
                []),
            actor);

        var chunk = await fixture.Documents.ReadContentAsync(
            source.DocumentId,
            0,
            200);
        Assert.Equal("Edited intro\n\n[Read](Target.md)", chunk.Content);
    }

    [Fact]
    public async Task MarkdownLinkResolvesWhenTargetIsCreatedLater()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                "notes/Source.md",
                null,
                VaultDocumentFormat.Markdown,
                "[Target](Target.md)",
                null),
            actor);
        var before = await fixture.Documents.GetMapAsync(source.DocumentId);
        Assert.Equal(
            "unresolved",
            Assert.Single(before.OutgoingLinks).ResolutionStatus);

        var target = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Target",
                "notes/Target.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Target",
                null),
            actor);

        var after = await fixture.Documents.GetMapAsync(source.DocumentId);
        var link = Assert.Single(after.OutgoingLinks);
        Assert.Equal(DocumentLinkKind.Markdown, link.LinkKind);
        Assert.Equal("resolved", link.ResolutionStatus);
        Assert.Equal(target.DocumentId, link.Target?.DocumentId);
    }

    [Fact]
    public async Task MultipleLinksAreRewrittenInOneSourceRevision()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var target = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Target",
                "notes/Target.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Target",
                null),
            actor);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                "docs/Source.md",
                null,
                VaultDocumentFormat.Markdown,
                "[[Target]] [One](../notes/Target.md) [Two](../notes/Target.md#Target)",
                null),
            actor);

        await fixture.Documents.UpdateAsync(
            target.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Renamed",
                "archive/Renamed.md",
                "# Renamed",
                []),
            actor);

        var sourceMap = await fixture.Documents.GetMapAsync(source.DocumentId);
        Assert.Equal(2, sourceMap.Metadata.Revision);
        var chunk = await fixture.Documents.ReadContentAsync(
            source.DocumentId,
            0,
            300);
        Assert.Equal(
            "[[Renamed]] [One](../archive/Renamed.md) [Two](../archive/Renamed.md#Target)",
            chunk.Content);
    }

    [Fact]
    public async Task LinkRewriteFailureRollsBackTargetAndSourceChanges()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var target = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Target",
                "notes/Target.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Target",
                null),
            actor);
        var source = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                "docs/Source.md",
                null,
                VaultDocumentFormat.Markdown,
                "[Target](../notes/Target.md)",
                null),
            actor);
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    $"""
                     CREATE TRIGGER fail_source_link_rewrite
                     BEFORE UPDATE OF content ON documents
                     WHEN OLD.document_id = '{source.DocumentId:D}'
                     BEGIN
                         SELECT RAISE(ABORT, 'forced link rewrite failure');
                     END;
                     """;
                return await command.Command.ExecuteNonQueryAsync();
            });

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.UpdateAsync(
                target.DocumentId,
                1,
                new UpdateDocumentRequest(
                    "Renamed",
                    "archive/Renamed.md",
                    "# Renamed",
                    []),
                actor));

        Assert.Equal(VaultErrorCode.DatabaseError, exception.Code);
        var targetAfter = await fixture.Documents.GetAsync(target.DocumentId);
        var sourceAfter = await fixture.Documents.GetAsync(source.DocumentId);
        Assert.Equal(("Target", "notes/Target.md", 1), (
            targetAfter.Title,
            targetAfter.VirtualPath,
            targetAfter.Revision));
        Assert.Equal(1, sourceAfter.Revision);
        var sourceContent = await fixture.Documents.ReadContentAsync(
            source.DocumentId,
            0,
            100);
        Assert.Equal("[Target](../notes/Target.md)", sourceContent.Content);
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
