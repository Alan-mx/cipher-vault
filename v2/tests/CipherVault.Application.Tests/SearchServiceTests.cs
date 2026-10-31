using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Search;

namespace CipherVault.Application.Tests;

public sealed class SearchServiceTests
{
    [Fact]
    public async Task FtsAndShortQuerySearchOnlyActiveDocuments()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var alpha = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Alpha",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "# Alpha\nalpha body",
                ["Work"]),
            actor);
        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Beta",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "beta body",
                ["Work"]),
            actor);

        var fts = await fixture.Search.SearchAsync(
            new SearchQuery(
                "alpha",
                ["Work"],
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest()));
        Assert.Single(fts.Hits);
        Assert.Equal(alpha.DocumentId, fts.Hits[0].DocumentId);

        var shortQuery = await fixture.Search.SearchAsync(
            new SearchQuery(
                "al",
                null,
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest()));
        Assert.Single(shortQuery.Hits);
        Assert.False(shortQuery.Coverage.Truncated);

        await fixture.Documents.DeleteAsync(alpha.DocumentId, 1, actor);
        var afterDelete = await fixture.Search.SearchAsync(
            new SearchQuery(
                "alpha",
                null,
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest()));
        Assert.Empty(afterDelete.Hits);
    }

    [Fact]
    public async Task CursorIsBoundToQueryAndFilters()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        foreach (var index in Enumerable.Range(1, 3))
        {
            await fixture.Documents.CreateAsync(
                new CreateDocumentRequest(
                    $"Doc {index}",
                    null,
                    null,
                    VaultDocumentFormat.Text,
                    "needle " + index,
                    null),
                actor);
        }

        var first = await fixture.Search.SearchAsync(
            new SearchQuery(
                "needle",
                null,
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest(1)));
        Assert.Single(first.Hits);
        Assert.NotNull(first.NextCursor);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Search.SearchAsync(
                new SearchQuery(
                    "other",
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    new CursorPageRequest(1, first.NextCursor))));
        Assert.Equal(VaultErrorCode.InvalidCursor, exception.Code);
    }

    [Fact]
    public async Task ShortQueryScansAtMostTenThousandDocuments()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var lease = context.CreateCommand();
                lease.Command.CommandText =
                    """
                    WITH RECURSIVE seq(value) AS (
                        VALUES(1)
                        UNION ALL
                        SELECT value + 1 FROM seq WHERE value < 10001
                    )
                    INSERT INTO documents(
                        document_id, title, title_key, path, path_key, format,
                        content, frontmatter_json, headings_json, revision,
                        created_at, updated_at, deleted_at, purge_eligible_at)
                    SELECT
                        lower(hex(randomblob(4))) || '-'
                        || lower(hex(randomblob(2))) || '-'
                        || lower(hex(randomblob(2))) || '-'
                        || lower(hex(randomblob(2))) || '-'
                        || lower(hex(randomblob(6))),
                        printf('Bulk %05d', value),
                        printf('BULK %05d', value),
                        printf('bulk/%05d.txt', value),
                        printf('bulk/%05d.txt', value),
                        'txt',
                        'body without the short query token',
                        '{}',
                        '[]',
                        1,
                        '2026-10-31T00:00:00.0000000+00:00',
                        '2026-10-31T00:00:00.0000000+00:00',
                        NULL,
                        NULL
                    FROM seq;
                    """;
                return await lease.Command.ExecuteNonQueryAsync();
            });

        var page = await fixture.Search.SearchAsync(
            new SearchQuery(
                "zz",
                null,
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest()));

        Assert.Empty(page.Hits);
        Assert.Equal(10001, page.Coverage.TotalActiveDocuments);
        Assert.Equal(10000, page.Coverage.ScannedDocuments);
        Assert.True(page.Coverage.Truncated);
    }
}
