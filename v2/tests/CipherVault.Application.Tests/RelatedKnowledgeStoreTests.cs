using System.Diagnostics;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.RelatedKnowledge;
using CipherVault.Core.Storage.RelatedKnowledge;

namespace CipherVault.Application.Tests;

public sealed class RelatedKnowledgeStoreTests
{
    [Fact]
    public async Task CandidateSourcesAreScoredAndDirectNeighborsAreExcluded()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var direct = await CreateAsync(
            fixture,
            actor,
            "Direct Project",
            "---\nproject: CipherVault\n---\n\nDirect.");
        var inbound = await CreateAsync(
            fixture,
            actor,
            "Inbound Project",
            "---\nproject: CipherVault\n---\n\nSee [[Redis Cache Strategy]].");
        _ = await CreateAsync(
            fixture,
            actor,
            "Overview",
            "---\nproject: CipherVault\n---\n\nOverview.");
        _ = await CreateAsync(
            fixture,
            actor,
            "Type Only",
            "---\ntype: note\n---\n\nType.");
        _ = await CreateAsync(
            fixture,
            actor,
            "Tag Only",
            "---\ntags: [Work, Redis]\n---\n\nTags.");
        _ = await CreateAsync(
            fixture,
            actor,
            "Property Only",
            "---\nstatus: active\nmaturity: 3\n---\n\nProperties.");
        _ = await CreateAsync(
            fixture,
            actor,
            "Redis Cache Strategy Notes",
            "Title only.");
        var quantum = await CreateAsync(
            fixture,
            actor,
            "Quantum Target",
            "Quantum target.");
        _ = await CreateAsync(
            fixture,
            actor,
            "Bridge Note",
            $"See [[{await GetTitleAsync(fixture, quantum)}]].");
        var trashed = await CreateAsync(
            fixture,
            actor,
            "Trashed Project",
            "---\nproject: CipherVault\n---\n\nTrash.");
        await fixture.Documents.DeleteAsync(trashed, 1, actor);

        var source = await CreateAsync(
            fixture,
            actor,
            "Redis Cache Strategy",
            """
            ---
            project: CipherVault
            type: note
            status: active
            maturity: 3
            tags: [Work, Redis]
            ---

            [[Direct Project]]
            [[Bridge Note]]
            """);

        var results = await fixture.QueryAsync(context =>
            RelatedKnowledgeStore.GetAsync(
                context,
                source,
                20,
                CancellationToken.None));

        var titles = results.Select(item => item.Title).ToArray();
        Assert.Contains("Overview", titles);
        Assert.Contains("Type Only", titles);
        Assert.Contains("Tag Only", titles);
        Assert.Contains("Property Only", titles);
        Assert.Contains("Quantum Target", titles);
        Assert.DoesNotContain("Direct Project", titles);
        Assert.DoesNotContain("Inbound Project", titles);
        Assert.DoesNotContain("Trashed Project", titles);
        Assert.DoesNotContain("Redis Cache Strategy Notes", titles);

        var overview = Assert.Single(
            results,
            item => item.Title == "Overview");
        Assert.Contains(
            overview.Reasons,
            reason => reason.Code == RelatedReasonCodes.SameProject);
        Assert.Equal(
            results.OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.UpdatedAt)
                .ThenBy(item => item.DocumentId)
                .Select(item => item.DocumentId),
            results.Select(item => item.DocumentId));
    }

    [Fact]
    public async Task InvalidLimitAndMissingSourceUseStableErrors()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();

        var invalid = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context =>
                RelatedKnowledgeStore.GetAsync(
                    context,
                    Guid.NewGuid(),
                    21,
                    CancellationToken.None)));
        Assert.Equal(VaultErrorCode.InvalidInput, invalid.Code);

        var missing = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context =>
                RelatedKnowledgeStore.GetAsync(
                    context,
                    Guid.NewGuid(),
                    10,
                    CancellationToken.None)));
        Assert.Equal(VaultErrorCode.DocumentNotFound, missing.Code);
    }

    [Fact]
    public async Task RelatedQueryPerformanceScalesAcrossDocumentCounts()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var source = await CreateAsync(
            fixture,
            actor,
            "Related Performance Source",
            "---\nproject: CipherVault\nstatus: active\n---\n\nSource.");

        await InsertPerformanceDocumentsAsync(fixture, 1, 100);
        var hundred = await MeasureRelatedAsync(fixture, source);
        Assert.True(
            hundred.Elapsed < TimeSpan.FromSeconds(10),
            $"100-document related query took {hundred.Elapsed}.");

        await InsertPerformanceDocumentsAsync(fixture, 101, 1_000);
        var thousand = await MeasureRelatedAsync(fixture, source);
        Assert.True(
            thousand.Elapsed < TimeSpan.FromSeconds(10),
            $"1000-document related query took {thousand.Elapsed}.");

        await InsertPerformanceDocumentsAsync(fixture, 1_001, 10_000);
        var tenThousand = await MeasureRelatedAsync(fixture, source);
        Assert.True(
            tenThousand.Elapsed < TimeSpan.FromSeconds(15),
            $"10000-document related query took {tenThousand.Elapsed}.");
    }

    [Fact]
    public async Task HighDegreeDirectNeighborsRemainExcluded()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        await InsertHubTargetsAsync(fixture, 300);
        var links = string.Join(
            '\n',
            Enumerable.Range(1, 300)
                .Select(index => $"[[Hub Target {index:00000}]]"));
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var source = await CreateAsync(
            fixture,
            actor,
            "Hub Source",
            $"""
             ---
             project: CipherVault
             status: active
             ---

             {links}
             """);

        var stopwatch = Stopwatch.StartNew();
        var results = await fixture.QueryAsync(context =>
            RelatedKnowledgeStore.GetAsync(
                context,
                source,
                20,
                CancellationToken.None));
        stopwatch.Stop();

        Assert.Empty(results);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"High-degree related query took {stopwatch.Elapsed}.");
    }

    private static async Task<Guid> CreateAsync(
        VaultApplicationFixture fixture,
        VaultActorContext actor,
        string title,
        string content)
    {
        var result = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                title,
                null,
                null,
                VaultDocumentFormat.Markdown,
                content,
                null),
            actor);
        return result.DocumentId;
    }

    private static async Task<string> GetTitleAsync(
        VaultApplicationFixture fixture,
        Guid documentId)
    {
        var metadata = await fixture.Documents.GetAsync(documentId);
        return metadata.Title;
    }

    private static async Task InsertPerformanceDocumentsAsync(
        VaultApplicationFixture fixture,
        int start,
        int end)
    {
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    WITH RECURSIVE seq(value) AS (
                        VALUES($start)
                        UNION ALL
                        SELECT value + 1 FROM seq WHERE value < $end
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
                        printf('Related Perf %05d', value),
                        printf('RELATED PERF %05d', value),
                        printf('related-perf/%05d.md', value),
                        printf('related-perf/%05d.md', value),
                        'md',
                        '# Related Perf',
                        json_object(
                            'project', 'CipherVault',
                            'status', 'active'
                        ),
                        '[]',
                        1,
                        '2026-10-31T00:00:00.0000000+00:00',
                        '2026-10-31T00:00:00.0000000+00:00',
                        NULL,
                        NULL
                    FROM seq;
                    """;
                command.Command.Parameters.AddWithValue("$start", start);
                command.Command.Parameters.AddWithValue("$end", end);
                return await command.Command.ExecuteNonQueryAsync();
            });
    }

    private static async Task InsertHubTargetsAsync(
        VaultApplicationFixture fixture,
        int count)
    {
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    WITH RECURSIVE seq(value) AS (
                        VALUES(1)
                        UNION ALL
                        SELECT value + 1 FROM seq WHERE value < $count
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
                        printf('Hub Target %05d', value),
                        printf('HUB TARGET %05d', value),
                        printf('hub/%05d.md', value),
                        printf('hub/%05d.md', value),
                        'md',
                        '# Hub Target',
                        json_object(
                            'project', 'CipherVault',
                            'status', 'active'
                        ),
                        '[]',
                        1,
                        '2026-10-31T00:00:00.0000000+00:00',
                        '2026-10-31T00:00:00.0000000+00:00',
                        NULL,
                        NULL
                    FROM seq;
                    """;
                command.Command.Parameters.AddWithValue("$count", count);
                return await command.Command.ExecuteNonQueryAsync();
            });
    }

    private static async Task<(int Count, TimeSpan Elapsed)>
        MeasureRelatedAsync(
            VaultApplicationFixture fixture,
            Guid source)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = await fixture.QueryAsync(context =>
            RelatedKnowledgeStore.GetAsync(
                context,
                source,
                20,
                CancellationToken.None));
        stopwatch.Stop();
        Assert.Equal(20, results.Count);
        return (results.Count, stopwatch.Elapsed);
    }
}
