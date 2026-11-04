using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class DocumentServiceCreateTests
{
    [Fact]
    public async Task MarkdownCreateInjectsStableDocumentId()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Identified",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "# Identified",
                null),
            actor);

        var content = await fixture.Documents.ReadContentAsync(
            created.DocumentId,
            0,
            500);
        Assert.Contains(
            $"id: {created.DocumentId:D}",
            content.Content,
            StringComparison.Ordinal);
        var map = await fixture.Documents.GetMapAsync(created.DocumentId);
        Assert.Contains(
            map.FrontMatterFields,
            field => field.Key == "id"
                && field.ValueType == "string"
                && field.ValueJson.Contains(
                    created.DocumentId.ToString("D"),
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task TextCreateDoesNotInjectIdentityIntoBody()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Plain",
                null,
                null,
                VaultDocumentFormat.Text,
                "plain body",
                null),
            actor);

        var content = await fixture.Documents.ReadContentAsync(
            created.DocumentId,
            0,
            100);
        Assert.Equal("plain body", content.Content);
    }

    [Fact]
    public async Task CreateRejectsCallerProvidedFrontmatterId()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.CreateAsync(
                new CreateDocumentRequest(
                    "Identified",
                    null,
                    null,
                    VaultDocumentFormat.Markdown,
                    $"---\nid: {Guid.NewGuid():D}\n---\nBody",
                    null),
                actor));

        Assert.Equal(VaultErrorCode.DocumentIdProtected, exception.Code);
    }

    [Fact]
    public async Task CreateWritesDocumentIndicesRevisionAndAuditAtomically()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi, "create-1");
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Target",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: [Work]\n---\n# Target\nBody",
                null),
            actor);

        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                null,
                "Notes",
                VaultDocumentFormat.Markdown,
                "---\ntags: [Work]\n---\n# Source\nSee [[Target]].",
                ["Work"]),
            actor);

        Assert.Equal(1, created.Revision);
        Assert.Equal("Notes/Source.md", created.VirtualPath);
        Assert.NotNull(created.AuditEventId);
        Assert.Equal(2, await fixture.QueryAsync(CountAsync("documents")));
        Assert.Equal(2, await fixture.QueryAsync(CountAsync("document_revisions")));
        Assert.Equal(2, await fixture.QueryAsync(CountAsync("document_fts")));
        Assert.Equal(2, await fixture.QueryAsync(CountAsync("audit_events")));
        Assert.Equal(1, await fixture.QueryAsync(CountAsync("tags")));
        Assert.Equal(2, await fixture.QueryAsync(CountAsync("document_tags")));
        Assert.Equal(
            1,
            await fixture.QueryAsync(
                async context =>
                {
                    using var lease = context.CreateCommand();
                    lease.Command.CommandText =
                        "SELECT count(*) FROM document_links WHERE resolution_status = 'resolved';";
                    return Convert.ToInt32(await lease.Command.ExecuteScalarAsync());
                }));
    }

    [Fact]
    public async Task PathConflictAndFrontMatterTagMismatchAreRejected()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Note",
                "same/Note.md",
                null,
                VaultDocumentFormat.Markdown,
                "body",
                null),
            actor);

        var conflict = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.CreateAsync(
                new CreateDocumentRequest(
                    "Other",
                    "same/Note.md",
                    null,
                    VaultDocumentFormat.Markdown,
                    "other",
                    null),
                actor));
        Assert.Equal(VaultErrorCode.DocumentPathConflict, conflict.Code);

        var mismatch = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.CreateAsync(
                new CreateDocumentRequest(
                    "Mismatch",
                    null,
                    null,
                    VaultDocumentFormat.Markdown,
                    "---\ntags: [one]\n---\nbody",
                    ["two"]),
                actor));
        Assert.Equal(VaultErrorCode.InvalidInput, mismatch.Code);
    }

    [Fact]
    public async Task UnresolvedLinkIsReconciledWhenTargetIsCreated()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Source",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "See [[Later]].",
                null),
            actor);

        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Later",
                null,
                null,
                VaultDocumentFormat.Text,
                "target",
                null),
            actor);

        var status = await fixture.QueryAsync(
            async context =>
            {
                using var lease = context.CreateCommand();
                lease.Command.CommandText =
                    """
                    SELECT resolution_status
                    FROM document_links
                    WHERE raw_target_key = 'LATER';
                    """;
                return Convert.ToString(await lease.Command.ExecuteScalarAsync());
            });
        Assert.Equal("resolved", status);
    }

    private static Func<CipherVault.Core.Database.VaultOperationContext, Task<int>>
        CountAsync(string table) =>
        async context =>
        {
            using var lease = context.CreateCommand();
            lease.Command.CommandText = $"SELECT count(*) FROM {table};";
            return Convert.ToInt32(await lease.Command.ExecuteScalarAsync());
        };
}
