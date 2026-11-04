using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Knowledge;
using CipherVault.Core.Storage.Knowledge;

namespace CipherVault.Application.Tests;

public sealed class KnowledgeViewStoreTests
{
    [Fact]
    public async Task ProjectFacetsMergeCaseLabelsAndApplyActiveOnly()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        _ = await CreateAsync(
            fixture,
            actor,
            "Project A Upper",
            "project: Project A\nstatus: active");
        _ = await CreateAsync(
            fixture,
            actor,
            "Project A Lower",
            "project: project a\nstatus: active");
        _ = await CreateAsync(
            fixture,
            actor,
            "Project B",
            "project: Project B\nstatus: planned");
        _ = await CreateAsync(
            fixture,
            actor,
            "Missing Project",
            "status: active");
        _ = await CreateAsync(
            fixture,
            actor,
            "Blank Project",
            "project: \"   \"\nstatus: active");
        _ = await CreateAsync(
            fixture,
            actor,
            "Array Project",
            "project:\n  - A\n  - B\nstatus: active");
        _ = await CreateAsync(
            fixture,
            actor,
            "Number Project",
            "project: 7\nstatus: active");
        _ = await CreateAsync(
            fixture,
            actor,
            "Planned Missing",
            "status: planned");
        _ = await CreateAsync(
            fixture,
            actor,
            "Uppercase Active",
            "project: Project C\nstatus: Active");
        var trashed = await CreateAsync(
            fixture,
            actor,
            "Trashed Project",
            "project: Project A\nstatus: active");
        await fixture.Documents.DeleteAsync(trashed, 1, actor);

        var active = await fixture.QueryAsync(context =>
            KnowledgeViewStore.GetFacetsAsync(
                context,
                KnowledgeViewField.Project,
                activeOnly: true,
                CancellationToken.None));
        Assert.Single(
            active.Items,
            item => item.Key == "PROJECT A" && !item.IsUnset);
        var projectA = Assert.Single(
            active.Items,
            item => item.Key == "PROJECT A");
        Assert.Equal("Project A", projectA.Label);
        Assert.Equal(2, projectA.DocumentCount);
        var unset = Assert.Single(active.Items, item => item.IsUnset);
        Assert.Equal("未设置", unset.Label);
        Assert.Equal(4, unset.DocumentCount);
        Assert.True(active.Items[^1].IsUnset);
        Assert.False(active.Truncated);

        var all = await fixture.QueryAsync(context =>
            KnowledgeViewStore.GetFacetsAsync(
                context,
                KnowledgeViewField.Project,
                activeOnly: false,
                CancellationToken.None));
        Assert.Equal(4, all.Items.Count);
        Assert.Equal(
            ["PROJECT A", "PROJECT B", "PROJECT C", null],
            all.Items.Select(item => item.Key));
        Assert.Equal(5, all.Items[^1].DocumentCount);
    }

    [Fact]
    public async Task FacetsTruncateAtTwoHundredValidItems()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    WITH RECURSIVE seq(value) AS (
                        VALUES(1)
                        UNION ALL
                        SELECT value + 1 FROM seq WHERE value < 201
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
                        printf('Project %03d', value),
                        printf('PROJECT %03d', value),
                        printf('projects/%03d.md', value),
                        printf('projects/%03d.md', value),
                        'md',
                        'body',
                        json_object(
                            'project', printf('Project %03d', value),
                            'status', 'active'
                        ),
                        '[]',
                        1,
                        '2026-11-04T00:00:00Z',
                        '2026-11-04T00:00:00Z',
                        NULL,
                        NULL
                    FROM seq;
                    """;
                return await command.Command.ExecuteNonQueryAsync();
            });

        var page = await fixture.QueryAsync(context =>
            KnowledgeViewStore.GetFacetsAsync(
                context,
                KnowledgeViewField.Project,
                activeOnly: true,
                CancellationToken.None));

        Assert.Equal(201, page.Items.Count);
        Assert.Equal(200, page.Items.Count(item => !item.IsUnset));
        Assert.True(page.Truncated);
        Assert.True(page.Items[^1].IsUnset);

        var outsideTopTwoHundred = await fixture.QueryAsync(context =>
            KnowledgeViewStore.GetDocumentsAsync(
                context,
                KnowledgeViewField.Project,
                "PROJECT 201",
                unset: false,
                activeOnly: false,
                new CursorPageRequest(30),
                CancellationToken.None));
        Assert.Single(outsideTopTwoHundred.Items);
    }

    [Fact]
    public async Task FacetsRejectActiveOnlyForNonProjectAndInvalidField()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();

        var activeOnly = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context =>
                KnowledgeViewStore.GetFacetsAsync(
                    context,
                    KnowledgeViewField.Type,
                    activeOnly: true,
                    CancellationToken.None)));
        Assert.Equal(VaultErrorCode.InvalidInput, activeOnly.Code);

        var field = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context =>
                KnowledgeViewStore.GetFacetsAsync(
                    context,
                    (KnowledgeViewField)999,
                    activeOnly: false,
                    CancellationToken.None)));
        Assert.Equal(VaultErrorCode.InvalidInput, field.Code);
    }

    [Fact]
    public async Task DocumentsFilterSortPageAndReadTags()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var first = await CreateAsync(
            fixture,
            actor,
            "View A One",
            "project: Project A\nstatus: active\ntags: [Work]");
        var second = await CreateAsync(
            fixture,
            actor,
            "View A Two",
            "project: project a\nstatus: active\ntags: [Work]");
        _ = await CreateAsync(
            fixture,
            actor,
            "View A Planned",
            "project: Project A\nstatus: planned");
        _ = await CreateAsync(
            fixture,
            actor,
            "View Unset Missing",
            "status: active");
        _ = await CreateAsync(
            fixture,
            actor,
            "View Unset Blank",
            "project: \"   \"\nstatus: active");
        _ = await CreateAsync(
            fixture,
            actor,
            "View Unset Array",
            "project:\n  - A\nstatus: active");
        _ = await CreateAsync(
            fixture,
            actor,
            "View Unset Planned",
            "status: planned");

        var firstPage = await fixture.QueryAsync(context =>
            KnowledgeViewStore.GetDocumentsAsync(
                context,
                KnowledgeViewField.Project,
                "project a",
                unset: false,
                activeOnly: true,
                new CursorPageRequest(1),
                CancellationToken.None));
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);
        Assert.Equal(["Work"], firstPage.Items[0].Tags);

        var secondPage = await fixture.QueryAsync(context =>
            KnowledgeViewStore.GetDocumentsAsync(
                context,
                KnowledgeViewField.Project,
                "PROJECT A",
                unset: false,
                activeOnly: true,
                new CursorPageRequest(1, firstPage.NextCursor),
                CancellationToken.None));
        Assert.Single(secondPage.Items);
        Assert.Null(secondPage.NextCursor);
        Assert.Equal(
            new[] { first, second }.OrderBy(id => id),
            firstPage.Items
                .Concat(secondPage.Items)
                .Select(item => item.DocumentId)
                .OrderBy(id => id));

        var unset = await fixture.QueryAsync(context =>
            KnowledgeViewStore.GetDocumentsAsync(
                context,
                KnowledgeViewField.Project,
                null,
                unset: true,
                activeOnly: true,
                new CursorPageRequest(30),
                CancellationToken.None));
        Assert.Equal(3, unset.Items.Count);

        var invalidParameter = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context =>
                KnowledgeViewStore.GetDocumentsAsync(
                    context,
                    KnowledgeViewField.Project,
                    "PROJECT A",
                    unset: true,
                    activeOnly: false,
                    new CursorPageRequest(),
                    CancellationToken.None)));
        Assert.Equal(VaultErrorCode.InvalidInput, invalidParameter.Code);

        var invalidActiveOnly = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context =>
                KnowledgeViewStore.GetDocumentsAsync(
                    context,
                    KnowledgeViewField.Type,
                    "NOTE",
                    unset: false,
                    activeOnly: true,
                    new CursorPageRequest(),
                    CancellationToken.None)));
        Assert.Equal(VaultErrorCode.InvalidInput, invalidActiveOnly.Code);

        var invalidPageSize = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context =>
                KnowledgeViewStore.GetDocumentsAsync(
                    context,
                    KnowledgeViewField.Project,
                    "PROJECT A",
                    unset: false,
                    activeOnly: false,
                    new CursorPageRequest(101),
                    CancellationToken.None)));
        Assert.Equal(VaultErrorCode.InvalidInput, invalidPageSize.Code);

        var invalidCursor = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context =>
                KnowledgeViewStore.GetDocumentsAsync(
                    context,
                    KnowledgeViewField.Project,
                    "PROJECT A",
                    unset: false,
                    activeOnly: false,
                    new CursorPageRequest(1, firstPage.NextCursor),
                    CancellationToken.None)));
        Assert.Equal(VaultErrorCode.InvalidCursor, invalidCursor.Code);
    }

    private static async Task<Guid> CreateAsync(
        VaultApplicationFixture fixture,
        VaultActorContext actor,
        string title,
        string frontMatter)
    {
        var result = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                title,
                null,
                null,
                VaultDocumentFormat.Markdown,
                $"""
                 ---
                 {frontMatter}
                 ---

                 Body.
                 """,
                null),
            actor);
        return result.DocumentId;
    }
}
