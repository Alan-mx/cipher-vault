using System.Diagnostics;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeGraph;
using CipherVault.Core.Relations;
using CipherVault.Core.Storage.Relations;
using Xunit.Abstractions;

namespace CipherVault.Application.Tests;

public sealed class RelationSuggestionPerformanceTests
{
    private const int DocumentCount = 10_000;
    private const int PendingCount = 5_000;
    private readonly ITestOutputHelper output;

    public RelationSuggestionPerformanceTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task RelationReviewAndGraphMeetScaleBudgets()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync(
            new ManualTimeProvider(
                DateTimeOffset.Parse("2026-11-06T00:00:00Z")));
        await InsertDocumentsAsync(fixture);
        await InsertSuggestionsAsync(fixture);
        await InsertRelationsAsync(fixture);
        var limitError = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationSuggestions.CreateAsync(
                new CreateRelationSuggestionRequest(
                    RelationType.Related,
                    DocumentId(2),
                    1,
                    DocumentId(3),
                    1,
                    "Limit check.",
                    "performance-model",
                    null,
                    null,
                    []),
                new VaultActorContext(VaultActorKind.Mcp),
                Guid.NewGuid()));
        Assert.Equal(
            VaultErrorCode.RelationSuggestionLimitExceeded,
            limitError.Code);

        var listTimes = new List<double>();
        string? cursor = null;
        var queryNow = fixture.TimeProvider.GetUtcNow();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var index = 0; index < 5; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            var page = await fixture.QueryAsync(
                context => RelationSuggestionStore.ListAsync(
                    context,
                    new RelationSuggestionQuery(
                        RelationSuggestionStatus.PendingReview,
                        null,
                        null,
                        null,
                        null,
                        null,
                        new CursorPageRequest(100, cursor)),
                    queryNow,
                    CancellationToken.None));
            stopwatch.Stop();
            listTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
            Assert.Equal(100, page.Items.Count);
            cursor = page.NextCursor;
        }

        var user = new VaultActorContext(VaultActorKind.UserUi);
        var detailTimes = new List<double>();
        for (var index = 0; index < 3; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            _ = await fixture.RelationSuggestions.GetAsync(
                SuggestionId(105_000),
                user);
            stopwatch.Stop();
            detailTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var graphTimes = new List<double>();
        for (var index = 0; index < 3; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            var graph = await fixture.Graph.GetLocalGraphAsync(
                new LocalGraphQuery(
                    DocumentId(1),
                    Depth: 1,
                    MaxNodes: 10,
                    MaxEdges: 100));
            stopwatch.Stop();
            graphTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
            Assert.Equal(2, graph.Edges.Count);
            Assert.All(
                graph.Edges,
                edge => Assert.Equal(
                    GraphEdgeKind.TypedRelation,
                    edge.EdgeKind));
        }

        var applyStopwatch = Stopwatch.StartNew();
        var applied = await fixture.RelationReview.ApplyAsync(
            SuggestionId(105_000),
            1,
            user,
            Guid.NewGuid());
        applyStopwatch.Stop();
        Assert.Equal(
            RelationSuggestionApplyResult.AlreadyExists,
            applied.ApplyResult);

        var staleStopwatch = Stopwatch.StartNew();
        _ = await fixture.Documents.UpdateAsync(
            DocumentId(1),
            1,
            new UpdateDocumentRequest(
                "Relation Perf 00001",
                "relation/00001.md",
                "# Relation Perf 00001\n\nUpdated.",
                []),
            user);
        staleStopwatch.Stop();

        var allocated = GC.GetTotalAllocatedBytes(precise: true)
            - allocatedBefore;
        var databaseBytes = new FileInfo(
            Path.Combine(fixture.VaultDirectory, "vault.db")).Length;
        var peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
        var listP50 = Percentile(listTimes, 0.50);
        var listP95 = Percentile(listTimes, 0.95);
        var detailP50 = Percentile(detailTimes, 0.50);
        var detailP95 = Percentile(detailTimes, 0.95);
        var graphP50 = Percentile(graphTimes, 0.50);
        var graphP95 = Percentile(graphTimes, 0.95);
        output.WriteLine(
            $"relation list p50={listP50:F1}ms p95={listP95:F1}ms "
            + $"allocated={allocated}");
        output.WriteLine(
            $"relation detail p50={detailP50:F1}ms p95={detailP95:F1}ms");
        output.WriteLine(
            $"relation apply elapsed={applyStopwatch.Elapsed.TotalMilliseconds:F1}ms");
        output.WriteLine(
            $"graph p50={graphP50:F1}ms p95={graphP95:F1}ms");
        output.WriteLine(
            $"stale hook suggestions={PendingCount - 1} "
            + $"elapsed={staleStopwatch.Elapsed.TotalMilliseconds:F1}ms");
        output.WriteLine(
            $"database bytes={databaseBytes} peakWorkingSet={peakWorkingSet}");

        Assert.True(
            listP95 < TimeSpan.FromSeconds(10).TotalMilliseconds,
            $"Relation list P95 was {listP95:F1}ms.");
        Assert.True(
            detailP95 < TimeSpan.FromSeconds(5).TotalMilliseconds,
            $"Relation detail P95 was {detailP95:F1}ms.");
        Assert.True(
            applyStopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Relation apply took {applyStopwatch.Elapsed}.");
        Assert.True(
            graphP95 < TimeSpan.FromSeconds(15).TotalMilliseconds,
            $"Graph P95 was {graphP95:F1}ms.");
        Assert.True(
            staleStopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Stale hook took {staleStopwatch.Elapsed}.");
    }

    private static async Task InsertDocumentsAsync(
        VaultApplicationFixture fixture) =>
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    $"""
                     WITH digits(value) AS (
                         VALUES(0), (1), (2), (3), (4),
                                (5), (6), (7), (8), (9)
                     ),
                     seq(value) AS (
                         SELECT
                             a.value
                             + b.value * 10
                             + c.value * 100
                             + d.value * 1000
                             + 1
                         FROM digits AS a
                         CROSS JOIN digits AS b
                         CROSS JOIN digits AS c
                         CROSS JOIN digits AS d
                     )
                     INSERT INTO documents(
                         document_id, title, title_key, path, path_key, format,
                         content, frontmatter_json, headings_json, revision,
                         created_at, updated_at, deleted_at, purge_eligible_at)
                     SELECT
                         printf(
                             '%08x-0000-4000-8000-%012x',
                             value,
                             value),
                         printf('Relation Perf %05d', value),
                         printf('RELATION PERF %05d', value),
                         printf('relation/%05d.md', value),
                         printf('relation/%05d.md', value),
                         'md',
                         'body',
                         json_object(),
                         '[]',
                         1,
                         '2026-11-04T00:00:00Z',
                         printf(
                             '2026-11-04T%02d:%02d:%02dZ',
                             value / 3600,
                             (value / 60) % 60,
                             value % 60),
                         NULL,
                         NULL
                     FROM seq;
                     """;
                return await command.Command.ExecuteNonQueryAsync();
            });

    private static async Task InsertSuggestionsAsync(
        VaultApplicationFixture fixture) =>
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    $"""
                     WITH digits(value) AS (
                         VALUES(0), (1), (2), (3), (4),
                                (5), (6), (7), (8), (9)
                     ),
                     seq(value) AS (
                         SELECT
                             a.value
                             + b.value * 10
                             + c.value * 100
                             + d.value * 1000
                             + 1
                         FROM digits AS a
                         CROSS JOIN digits AS b
                         CROSS JOIN digits AS c
                         CROSS JOIN digits AS d
                         WHERE a.value
                             + b.value * 10
                             + c.value * 100
                             + d.value * 1000
                             < {PendingCount}
                     )
                     INSERT INTO document_relation_suggestions(
                         suggestion_id, suggestion_revision, payload_version,
                         relation_type, source_document_id,
                         source_document_revision, target_document_id,
                         target_document_revision, reason, model_name,
                         evidence_json, status, source_actor,
                         created_at, updated_at)
                     SELECT
                         printf(
                             '%08x-0000-4000-8000-%012x',
                             value + 100000,
                             value + 100000),
                         1,
                         1,
                         'depends_on',
                         printf(
                             '%08x-0000-4000-8000-%012x',
                             1,
                             1),
                         1,
                         printf(
                             '%08x-0000-4000-8000-%012x',
                             value + 1,
                             value + 1),
                         1,
                         'Synthetic performance suggestion.',
                         'performance-model',
                         '[]',
                         'pending_review',
                         'mcp',
                         '2026-11-05T00:00:00Z',
                         printf(
                             '2026-11-05T%02d:%02d:%02dZ',
                             value / 3600,
                             (value / 60) % 60,
                             value % 60)
                     FROM seq;
                     """;
                return await command.Command.ExecuteNonQueryAsync();
            });

    private static async Task InsertRelationsAsync(
        VaultApplicationFixture fixture) =>
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    WITH digits(value) AS (
                        VALUES(0), (1), (2), (3), (4),
                               (5), (6), (7), (8), (9)
                    ),
                    seq(value) AS (
                        SELECT
                            a.value
                            + b.value * 10
                            + c.value * 100
                            + d.value * 1000
                            + 1
                        FROM digits AS a
                        CROSS JOIN digits AS b
                        CROSS JOIN digits AS c
                        CROSS JOIN digits AS d
                        WHERE a.value
                            + b.value * 10
                            + c.value * 100
                            + d.value * 1000
                            < 5000
                    ),
                    relation_types(relation_type) AS (
                        VALUES('depends_on'), ('implements')
                    )
                    INSERT INTO document_relations(
                        relation_id, relation_type, source_document_id,
                        target_document_id, status, created_by_actor,
                        created_at, updated_at)
                    SELECT
                        printf(
                            '%08x-0000-4000-8000-%012x',
                            seq.value + 200000
                                + CASE relation_types.relation_type
                                    WHEN 'implements' THEN 10000
                                    ELSE 0
                                  END,
                            seq.value + 200000
                                + CASE relation_types.relation_type
                                    WHEN 'implements' THEN 10000
                                    ELSE 0
                                  END),
                        relation_types.relation_type,
                        printf(
                            '%08x-0000-4000-8000-%012x',
                            seq.value,
                            seq.value),
                        printf(
                            '%08x-0000-4000-8000-%012x',
                            seq.value + 5000,
                            seq.value + 5000),
                        'active',
                        'user_ui',
                        '2026-11-05T00:00:00Z',
                        '2026-11-05T00:00:00Z'
                    FROM seq
                    CROSS JOIN relation_types;
                    """;
                return await command.Command.ExecuteNonQueryAsync();
            });

    private static Guid DocumentId(int value) =>
        Guid.Parse($"{value:x8}-0000-4000-8000-{value:x12}");

    private static Guid SuggestionId(int value) =>
        Guid.Parse($"{value:x8}-0000-4000-8000-{value:x12}");

    private static double Percentile(
        IReadOnlyList<double> values,
        double percentile)
    {
        var ordered = values.Order().ToArray();
        var index = (int)Math.Ceiling((ordered.Length - 1) * percentile);
        return ordered[index];
    }
}
