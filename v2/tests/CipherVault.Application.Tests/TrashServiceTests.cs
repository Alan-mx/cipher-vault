using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class TrashServiceTests
{
    [Fact]
    public async Task RestoreRejectsPathConflictAndRebuildsIndices()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var target = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Target",
                "Target.txt",
                null,
                VaultDocumentFormat.Text,
                "target",
                ["Tag"]),
            actor);
        await fixture.Documents.DeleteAsync(target.DocumentId, 1, actor);
        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Replacement",
                "Target.txt",
                null,
                VaultDocumentFormat.Text,
                "replacement",
                null),
            actor);

        var conflict = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Trash.RestoreAsync(
                target.DocumentId,
                2,
                null,
                actor));
        Assert.True(
            conflict.Code is VaultErrorCode.DocumentPathConflict
                or VaultErrorCode.DatabaseError);

        var restored = await fixture.Trash.RestoreAsync(
            target.DocumentId,
            2,
            "Restored.txt",
            actor);
        Assert.Equal(3, restored.Revision);
        var metadata = await fixture.Documents.GetAsync(target.DocumentId);
        Assert.Equal("Restored.txt", metadata.VirtualPath);
        Assert.Equal(["Tag"], metadata.Tags);
    }

    [Fact]
    public async Task PurgeWaitsForEligibilityAndPreservesAudit()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await VaultApplicationFixture.CreateAsync(time);
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Purge",
                null,
                null,
                VaultDocumentFormat.Text,
                "body",
                ["Only"]),
            actor);
        await fixture.Documents.DeleteAsync(created.DocumentId, 1, actor);

        var notEligible = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Trash.PurgeAsync(created.DocumentId, 2, actor));
        Assert.Equal(VaultErrorCode.PurgeNotEligible, notEligible.Code);

        await fixture.QueryAsync(
            async context =>
            {
                using var lease = context.CreateCommand();
                lease.Command.CommandText =
                    """
                    UPDATE documents
                    SET purge_eligible_at = $now
                    WHERE document_id = $id;
                    """;
                lease.Command.Parameters.AddWithValue(
                    "$now",
                    time.GetUtcNow().ToString("O"));
                lease.Command.Parameters.AddWithValue("$id", created.DocumentId.ToString("D"));
                return await lease.Command.ExecuteNonQueryAsync();
            });
        var auditId = await fixture.Trash.PurgeAsync(created.DocumentId, 2, actor);
        Assert.NotNull(auditId);
        var counts = await fixture.QueryAsync(
            async context =>
            {
                using var lease = context.CreateCommand();
                lease.Command.CommandText =
                    """
                    SELECT
                      (SELECT count(*) FROM documents WHERE document_id = $id),
                      (SELECT count(*) FROM document_revisions WHERE document_id = $id),
                      (SELECT count(*) FROM audit_events WHERE action = 'purge');
                    """;
                lease.Command.Parameters.AddWithValue("$id", created.DocumentId.ToString("D"));
                await using var reader = await lease.Command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                return (
                    Documents: reader.GetInt32(0),
                    Revisions: reader.GetInt32(1),
                    PurgeAudits: reader.GetInt32(2));
            });

        Assert.Equal(0, counts.Documents);
        Assert.Equal(0, counts.Revisions);
        Assert.Equal(1, counts.PurgeAudits);
    }

    [Fact]
    public async Task McpCannotPurgeOrRestoreTrash()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Mcp",
                null,
                null,
                VaultDocumentFormat.Text,
                "body",
                null),
            mcp);
        await fixture.Documents.DeleteAsync(created.DocumentId, 1, mcp);

        var restore = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Trash.RestoreAsync(created.DocumentId, 2, null, mcp));
        Assert.Equal(VaultErrorCode.Forbidden, restore.Code);
        var purge = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Trash.PurgeAsync(created.DocumentId, 2, mcp));
        Assert.Equal(VaultErrorCode.Forbidden, purge.Code);
    }
}
