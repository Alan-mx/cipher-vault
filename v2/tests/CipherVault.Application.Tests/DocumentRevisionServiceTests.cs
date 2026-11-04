using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class DocumentRevisionServiceTests
{
    [Fact]
    public async Task RestorePreservesCurrentIdentityForLegacySnapshot()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Identity history",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "version one",
                null),
            actor);
        await fixture.Documents.UpdateAsync(
            created.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Identity history",
                null,
                "version two",
                []),
            actor);
        var legacyRevisionId = Guid.NewGuid();
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    INSERT INTO document_revisions(
                        revision_id, document_id, revision, title, path, format,
                        content, frontmatter_json, headings_json, tags_json,
                        created_at, retain_until)
                    VALUES(
                        $revisionId, $documentId, 99,
                        'Identity history', 'Identity history.md', 'md',
                        'legacy snapshot', '{}', '[]', '[]',
                        '2026-11-04T00:00:00Z',
                        '2099-11-04T00:00:00Z');
                    """;
                command.Command.Parameters.AddWithValue(
                    "$revisionId",
                    legacyRevisionId.ToString("D"));
                command.Command.Parameters.AddWithValue(
                    "$documentId",
                    created.DocumentId.ToString("D"));
                return await command.Command.ExecuteNonQueryAsync();
            });

        _ = await fixture.Revisions.RestoreAsync(
            created.DocumentId,
            legacyRevisionId,
            2,
            actor);

        var content = await fixture.Documents.ReadContentAsync(
            created.DocumentId,
            0,
            500);
        Assert.Contains(
            $"id: {created.DocumentId:D}",
            content.Content,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "legacy snapshot",
            content.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreRevisionCreatesNewCurrentRevision()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "History",
                null,
                null,
                VaultDocumentFormat.Text,
                "version one",
                null),
            actor);
        await fixture.Documents.UpdateAsync(
            created.DocumentId,
            1,
            new UpdateDocumentRequest(
                "History",
                null,
                "version two",
                []),
            actor);
        var revisions = await fixture.Revisions.ListAsync(
            created.DocumentId,
            new CursorPageRequest());
        Assert.Equal(2, revisions.Items.Count);
        var first = revisions.Items.Single(item => item.Revision == 1);

        var restored = await fixture.Revisions.RestoreAsync(
            created.DocumentId,
            first.RevisionId,
            2,
            actor);

        Assert.Equal(3, restored.Revision);
        var content = await fixture.Documents.ReadContentAsync(created.DocumentId, 0, 100);
        Assert.Equal("version one", content.Content);
        var after = await fixture.Revisions.ListAsync(
            created.DocumentId,
            new CursorPageRequest());
        Assert.Equal(3, after.Items.Count);
        Assert.Equal(3, after.Items[0].Revision);
    }

    [Fact]
    public async Task CleanupOnlyRemovesExpiredSnapshotsAndIsSystemOnly()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var system = new VaultActorContext(VaultActorKind.System);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Cleanup",
                null,
                null,
                VaultDocumentFormat.Text,
                "one",
                null),
            user);
        await fixture.Documents.UpdateAsync(
            created.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Cleanup",
                null,
                "two",
                []),
            user);
        await fixture.QueryAsync(
            async context =>
            {
                using var lease = context.CreateCommand();
                lease.Command.CommandText =
                    """
                    INSERT INTO document_revisions(
                        revision_id, document_id, revision, title, path, format,
                        content, frontmatter_json, headings_json, tags_json,
                        created_at, retain_until)
                    VALUES(
                        $revisionId, $id, 99, 'Cleanup', 'Cleanup.txt', 'txt',
                        'expired', '{}', '[]', '[]',
                        '2000-01-01T00:00:00.0000000Z',
                        '2000-01-01T00:00:00.0000000Z');
                    """;
                lease.Command.Parameters.AddWithValue(
                    "$revisionId",
                    Guid.NewGuid().ToString("D"));
                lease.Command.Parameters.AddWithValue("$id", created.DocumentId.ToString("D"));
                return await lease.Command.ExecuteNonQueryAsync();
            });

        var forbidden = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Revisions.CleanupExpiredAsync(user));
        Assert.Equal(VaultErrorCode.Forbidden, forbidden.Code);

        var cleanup = await fixture.Revisions.CleanupExpiredAsync(system);
        Assert.Equal(1, cleanup.DeletedSnapshots);
        Assert.NotNull(cleanup.AuditEventId);
        var remaining = await fixture.Revisions.ListAsync(
            created.DocumentId,
            new CursorPageRequest());
        Assert.Equal(2, remaining.Items.Count);
        Assert.Equal([2, 1], remaining.Items.Select(item => item.Revision));
    }
}
