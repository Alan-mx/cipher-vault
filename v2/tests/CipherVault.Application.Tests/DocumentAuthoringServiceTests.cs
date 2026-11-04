using CipherVault.Application.Documents;
using CipherVault.Core.Audit;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Search;

namespace CipherVault.Application.Tests;

public sealed class DocumentAuthoringServiceTests
{
    [Fact]
    public async Task AppendIsExactAndAllowsExistingDocumentMetadataUpdateAboveNormalWriteLimit()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.EnableDirectWriteAsync();
        var actor = new VaultActorContext(VaultActorKind.Mcp);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Large",
                null,
                null,
                VaultDocumentFormat.Text,
                "start",
                ["Work"]),
            actor);
        var firstChunk = new string('a', 200_000);
        var secondChunk = new string('b', 200_000);

        var appended = await fixture.Authoring.AppendAsync(
            created.DocumentId,
            1,
            firstChunk,
            actor);
        Assert.Equal(2, appended.Revision);
        var appendedAgain = await fixture.Authoring.AppendAsync(
            created.DocumentId,
            2,
            secondChunk,
            actor);
        Assert.Equal(3, appendedAgain.Revision);

        var content = await ReadAllAsync(fixture, created.DocumentId);
        Assert.Equal("start" + firstChunk + secondChunk, content);

        var renamed = await fixture.Authoring.UpdatePartialAsync(
            created.DocumentId,
            3,
            new DocumentPartialUpdate(
                OptionalField<string>.Of("Large Renamed"),
                OptionalField<string>.Missing,
                OptionalField<string>.Missing,
                OptionalField<IReadOnlyList<string>>.Missing),
            actor);
        Assert.Equal(4, renamed.Revision);
        Assert.Equal(
            "Large Renamed",
            (await fixture.Documents.GetAsync(created.DocumentId)).Title);
    }

    [Fact]
    public async Task PartialUpdatePreservesOmittedValuesAndRewritesAuthoritativeTags()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.EnableDirectWriteAsync();
        var actor = new VaultActorContext(VaultActorKind.Mcp);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Tagged",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: [Old]\nunknown: kept\n---\nBody",
                null),
            actor);

        var updated = await fixture.Authoring.UpdatePartialAsync(
            created.DocumentId,
            1,
            new DocumentPartialUpdate(
                OptionalField<string>.Missing,
                OptionalField<string>.Missing,
                OptionalField<string>.Missing,
                OptionalField<IReadOnlyList<string>>.Of(["New"])),
            actor);

        Assert.Equal(2, updated.Revision);
        var map = await fixture.Documents.GetMapAsync(created.DocumentId);
        Assert.Equal(["New"], map.Metadata.Tags);
        var content = await ReadAllAsync(fixture, created.DocumentId);
        Assert.Contains("unknown: kept", content, StringComparison.Ordinal);
        Assert.Contains("Body", content, StringComparison.Ordinal);
        Assert.DoesNotContain("tags: [Old]", content, StringComparison.Ordinal);

        var cleared = await fixture.Authoring.UpdatePartialAsync(
            created.DocumentId,
            2,
            new DocumentPartialUpdate(
                OptionalField<string>.Missing,
                OptionalField<string>.Missing,
                OptionalField<string>.Missing,
                OptionalField<IReadOnlyList<string>>.Of([])),
            actor);

        Assert.Equal(3, cleared.Revision);
        Assert.Empty((await fixture.Documents.GetAsync(created.DocumentId)).Tags);
        Assert.Contains(
            "tags: []",
            await ReadAllAsync(fixture, created.DocumentId),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialUpdateRejectsExplicitNullAndEmptyChanges()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.EnableDirectWriteAsync();
        var actor = new VaultActorContext(VaultActorKind.Mcp);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Validate",
                null,
                null,
                VaultDocumentFormat.Text,
                "body",
                null),
            actor);

        var empty = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Authoring.UpdatePartialAsync(
                created.DocumentId,
                1,
                new DocumentPartialUpdate(
                    OptionalField<string>.Missing,
                    OptionalField<string>.Missing,
                    OptionalField<string>.Missing,
                    OptionalField<IReadOnlyList<string>>.Missing),
                actor));
        Assert.Equal(VaultErrorCode.InvalidInput, empty.Code);

        var explicitNull = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Authoring.UpdatePartialAsync(
                created.DocumentId,
                1,
                new DocumentPartialUpdate(
                    OptionalField<string>.Of(null!),
                    OptionalField<string>.Missing,
                    OptionalField<string>.Missing,
                    OptionalField<IReadOnlyList<string>>.Missing),
                actor));
        Assert.Equal(VaultErrorCode.InvalidInput, explicitNull.Code);
    }

    [Fact]
    public async Task TagsOnlyUpdateConvertsScalarFrontMatterTagsToArray()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.EnableDirectWriteAsync();
        var actor = new VaultActorContext(VaultActorKind.Mcp);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Scalar Tags",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: Old\n---\nbody",
                null),
            actor);

        _ = await fixture.Authoring.UpdatePartialAsync(
            created.DocumentId,
            1,
            new DocumentPartialUpdate(
                OptionalField<string>.Missing,
                OptionalField<string>.Missing,
                OptionalField<string>.Missing,
                OptionalField<IReadOnlyList<string>>.Of(["New"])),
            actor);

        var metadata = await fixture.Documents.GetAsync(created.DocumentId);
        Assert.Equal(["New"], metadata.Tags);
    }

    [Fact]
    public async Task ContentOnlyMarkdownUpdateDerivesFrontMatterTags()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.EnableDirectWriteAsync();
        var actor = new VaultActorContext(VaultActorKind.Mcp);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Content Only",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: [Old]\n---\nbody",
                null),
            actor);

        var updated = await fixture.Authoring.UpdatePartialAsync(
            created.DocumentId,
            1,
            new DocumentPartialUpdate(
                OptionalField<string>.Of("Content Only"),
                OptionalField<string>.Missing,
                OptionalField<string>.Of("---\ntags: [New]\n---\nupdated"),
                OptionalField<IReadOnlyList<string>>.Missing),
            actor);

        Assert.Equal(2, updated.Revision);
        Assert.Equal(
            ["New"],
            (await fixture.Documents.GetAsync(created.DocumentId)).Tags);
    }

    [Fact]
    public async Task PartialUpdateRejectsMismatchedTagsWithoutChangingRevision()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.EnableDirectWriteAsync();
        var actor = new VaultActorContext(VaultActorKind.Mcp);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Matching Tags",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: [Old]\n---\nbody",
                null),
            actor);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Authoring.UpdatePartialAsync(
                created.DocumentId,
                1,
                new DocumentPartialUpdate(
                    OptionalField<string>.Missing,
                    OptionalField<string>.Missing,
                    OptionalField<string>.Of(
                        "---\ntags: [New]\n---\nbody"),
                    OptionalField<IReadOnlyList<string>>.Of(["Other"])),
                actor));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
        Assert.Equal(
            1,
            (await fixture.Documents.GetAsync(created.DocumentId)).Revision);
    }

    [Fact]
    public async Task PartialUpdateSizeAndNoOpDoNotAdvanceRevisionOrAudit()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.EnableDirectWriteAsync();
        var actor = new VaultActorContext(VaultActorKind.Mcp);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Partial Bounds",
                null,
                null,
                VaultDocumentFormat.Text,
                "body",
                null),
            actor);

        var tooLarge = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Authoring.UpdatePartialAsync(
                created.DocumentId,
                1,
                new DocumentPartialUpdate(
                    OptionalField<string>.Missing,
                    OptionalField<string>.Missing,
                    OptionalField<string>.Of(
                        new string('x', (256 * 1024) + 1)),
                    OptionalField<IReadOnlyList<string>>.Missing),
                actor));
        Assert.Equal(VaultErrorCode.DocumentTooLarge, tooLarge.Code);

        var noOp = await fixture.Authoring.UpdatePartialAsync(
            created.DocumentId,
            1,
            new DocumentPartialUpdate(
                OptionalField<string>.Missing,
                OptionalField<string>.Missing,
                OptionalField<string>.Of("body"),
                OptionalField<IReadOnlyList<string>>.Missing),
            actor);
        Assert.Equal(1, noOp.Revision);
        Assert.Null(noOp.AuditEventId);
        var audit = await fixture.Audit.ListAsync(
            new AuditQuery(
                VaultActorKind.Mcp,
                "update",
                created.DocumentId,
                null,
                null,
                new CursorPageRequest()));
        Assert.Empty(audit.Items);
    }

    [Fact]
    public async Task ContentAndMatchingTagsUpdateIndicesAndMcpAudit()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.EnableDirectWriteAsync();
        var actor = new VaultActorContext(VaultActorKind.Mcp, "authoring-audit");
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Searchable",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "old body",
                ["Old"]),
            actor);

        var updated = await fixture.Authoring.UpdatePartialAsync(
            created.DocumentId,
            1,
            new DocumentPartialUpdate(
                OptionalField<string>.Missing,
                OptionalField<string>.Missing,
                OptionalField<string>.Of(
                    "---\ntags: [New]\n---\n# Updated\nsearchable-heartbeat"),
                OptionalField<IReadOnlyList<string>>.Of(["New"])),
            actor);

        Assert.Equal(2, updated.Revision);
        var map = await fixture.Documents.GetMapAsync(created.DocumentId);
        Assert.Equal(["New"], map.Metadata.Tags);
        Assert.Contains(
            map.Headings,
            heading => heading.Text == "Updated");
        var search = await fixture.Search.SearchAsync(
            new SearchQuery(
                "searchable-heartbeat",
                null,
                VaultDocumentFormat.Markdown,
                null,
                null,
                null,
                false,
                new CursorPageRequest()));
        Assert.Contains(
            search.Hits,
            hit => hit.DocumentId == created.DocumentId);
        var audit = await fixture.Audit.ListAsync(
            new AuditQuery(
                VaultActorKind.Mcp,
                "update",
                created.DocumentId,
                null,
                null,
                new CursorPageRequest()));
        var updateEvent = Assert.Single(audit.Items);
        Assert.Equal(2, updateEvent.Revision);
        Assert.Equal(VaultActorKind.Mcp, updateEvent.Actor);
    }

    [Fact]
    public async Task OversizedAppendLeavesRevisionIndexAndAuditUnchanged()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.EnableDirectWriteAsync();
        var actor = new VaultActorContext(VaultActorKind.Mcp);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Bounded",
                null,
                null,
                VaultDocumentFormat.Text,
                "seed",
                null),
            actor);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Authoring.AppendAsync(
                created.DocumentId,
                1,
                new string('x', (256 * 1024) + 1),
                actor));
        Assert.Equal(VaultErrorCode.DocumentTooLarge, exception.Code);

        var metadata = await fixture.Documents.GetAsync(created.DocumentId);
        Assert.Equal(1, metadata.Revision);
        var counts = await fixture.QueryAsync(
            async context =>
            {
                using var lease = context.CreateCommand();
                lease.Command.CommandText =
                    """
                    SELECT
                      (SELECT count(*) FROM document_revisions WHERE document_id = $id),
                      (SELECT count(*) FROM document_fts WHERE document_id = $id),
                      (SELECT count(*) FROM audit_events
                       WHERE document_id = $id AND action = 'update');
                    """;
                lease.Command.Parameters.AddWithValue(
                    "$id",
                    created.DocumentId.ToString("D"));
                await using var reader = await lease.Command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                return (
                    Revisions: reader.GetInt32(0),
                    FtsRows: reader.GetInt32(1),
                    Updates: reader.GetInt32(2));
            });

        Assert.Equal(1, counts.Revisions);
        Assert.Equal(1, counts.FtsRows);
        Assert.Equal(0, counts.Updates);
    }

    private static async Task<string> ReadAllAsync(
        VaultApplicationFixture fixture,
        Guid documentId)
    {
        var builder = new System.Text.StringBuilder();
        var offset = 0;
        while (true)
        {
            var chunk = await fixture.Documents.ReadContentAsync(
                documentId,
                offset,
                65_536);
            builder.Append(chunk.Content);
            if (chunk.Done)
            {
                return builder.ToString();
            }
            offset = chunk.NextOffset;
        }
    }
}
