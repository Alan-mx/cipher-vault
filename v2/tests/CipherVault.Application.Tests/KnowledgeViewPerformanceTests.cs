using System.Diagnostics;
using CipherVault.Core.Documents;
using CipherVault.Core.Knowledge;
using CipherVault.Core.Storage.Knowledge;
using Xunit.Abstractions;

namespace CipherVault.Application.Tests;

public sealed class KnowledgeViewPerformanceTests
{
    private readonly ITestOutputHelper output;

    public KnowledgeViewPerformanceTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task FacetsAndDocumentsMeetScaleBudgets()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        await InsertDocumentsAsync(fixture, 1, 100);
        await MeasureFacetsAsync(fixture, 100);
        await InsertDocumentsAsync(fixture, 101, 1_000);
        await MeasureFacetsAsync(fixture, 1_000);
        await InsertDocumentsAsync(fixture, 1_001, 10_000);
        await MeasureFacetsAsync(fixture, 10_000);
        await MeasureDocumentsAsync(fixture);
        var maturityTimes = await MeasureMaturityQueueAsync(fixture);

        var facetPlan = await ReadPlanAsync(
            fixture,
            KnowledgeViewStore.FacetSql,
            includeDocumentParameters: false);
        var documentPlan = await ReadPlanAsync(
            fixture,
            KnowledgeViewStore.BuildDocumentSql(hasCursor: false),
            includeDocumentParameters: true);
        var maturityPlan = await ReadMaturityQueuePlanAsync(fixture);
        output.WriteLine(
            $"facets plan={string.Join(" | ", facetPlan)}");
        output.WriteLine(
            $"documents plan={string.Join(" | ", documentPlan)}");
        output.WriteLine(
            $"maturity queue plan={string.Join(" | ", maturityPlan)}");
        Assert.Contains(
            facetPlan,
            line => line.StartsWith(
                "SCAN d USING INDEX",
                StringComparison.Ordinal));
        Assert.True(
            facetPlan.Count(line => line.StartsWith(
                "SCAN d USING INDEX",
                StringComparison.Ordinal)) >= 2);

        Assert.Contains(
            documentPlan,
            line =>
                line.Contains("SCAN d", StringComparison.Ordinal)
                && line.Contains(
                    "ix_documents_active_updated",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            documentPlan,
            line => line.Contains(
                "USE TEMP B-TREE FOR ORDER BY",
                StringComparison.Ordinal));

        var facetScanSteps = await ReadFacetScanStepsAsync(fixture);
        var documentScanSteps = await ReadDocumentScanStepsAsync(fixture);
        var maturityScanSteps = await ReadMaturityQueueScanStepsAsync(fixture);
        var maturityP50 = Percentile(maturityTimes, 0.50);
        var maturityP95 = Percentile(maturityTimes, 0.95);
        output.WriteLine($"facet fullscan steps={facetScanSteps}");
        output.WriteLine($"document fullscan steps={documentScanSteps}");
        output.WriteLine($"maturity queue fullscan steps={maturityScanSteps}");
        output.WriteLine(
            $"maturity queue p50={maturityP50:F1}ms p95={maturityP95:F1}ms");
        Assert.True(facetScanSteps > 0);
        Assert.True(documentScanSteps > 0);
        Assert.True(maturityScanSteps > 0);
        Assert.True(
            maturityP95 < TimeSpan.FromSeconds(3).TotalMilliseconds,
            $"Maturity queue P95 was {maturityP95:F1}ms.");
    }

    [Fact]
    public async Task MaturityQueueRecordsColdAndWarmQueries()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        await InsertDocumentsAsync(fixture, 1, 10_000);

        var cold = Stopwatch.StartNew();
        _ = await fixture.MaturityQueue.ListAsync(
            KnowledgeMaturityQueueBucket.PendingOrganization,
            new CursorPageRequest(100));
        cold.Stop();

        var warmTimes = new List<double>();
        for (var index = 0; index < 5; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            _ = await fixture.MaturityQueue.ListAsync(
                KnowledgeMaturityQueueBucket.PendingOrganization,
                new CursorPageRequest(100));
            stopwatch.Stop();
            warmTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        var warmP95 = Percentile(warmTimes, 0.95);
        output.WriteLine(
            $"maturity queue cold={cold.Elapsed.TotalMilliseconds:F1}ms "
            + $"warmP95={warmP95:F1}ms");
        Assert.True(cold.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(warmP95 < TimeSpan.FromSeconds(3).TotalMilliseconds);
    }

    private async Task MeasureFacetsAsync(
        VaultApplicationFixture fixture,
        int documentCount)
    {
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        var page = await fixture.QueryAsync(context =>
            KnowledgeViewStore.GetFacetsAsync(
                context,
                KnowledgeViewField.Project,
                activeOnly: true,
                CancellationToken.None));
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true)
            - allocatedBefore;

        output.WriteLine(
            $"facets documents={documentCount}, items={page.Items.Count}, "
            + $"truncated={page.Truncated}, elapsed={stopwatch.ElapsedMilliseconds}ms, "
            + $"allocated={allocated}");
        Assert.True(page.Items.Count <= 201);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"{documentCount}-document facets took {stopwatch.Elapsed}.");
    }

    private async Task MeasureDocumentsAsync(
        VaultApplicationFixture fixture)
    {
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        var page = await fixture.QueryAsync(context =>
            KnowledgeViewStore.GetDocumentsAsync(
                context,
                KnowledgeViewField.Project,
                "PROJECT 001",
                unset: false,
                activeOnly: true,
                new CursorPageRequest(100),
                CancellationToken.None));
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true)
            - allocatedBefore;
        output.WriteLine(
            $"documents=10000, rows={page.Items.Count}, "
            + $"elapsed={stopwatch.ElapsedMilliseconds}ms, allocated={allocated}");
        Assert.NotEmpty(page.Items);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"10,000-document page took {stopwatch.Elapsed}.");
    }

    private async Task<IReadOnlyList<double>> MeasureMaturityQueueAsync(
        VaultApplicationFixture fixture)
    {
        var times = new List<double>();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        foreach (var bucket in new[]
                 {
                     KnowledgeMaturityQueueBucket.PendingOrganization,
                     KnowledgeMaturityQueueBucket.PendingVerification,
                     KnowledgeMaturityQueueBucket.Completed,
                     KnowledgeMaturityQueueBucket.Other
                 })
        {
            for (var index = 0; index < 3; index++)
            {
                var stopwatch = Stopwatch.StartNew();
                var page = await fixture.QueryAsync(context =>
                    KnowledgeMaturityQueueStore.ListAsync(
                        context,
                        new KnowledgeMaturityQueueQuery(
                            bucket,
                            new CursorPageRequest(100)),
                        CancellationToken.None));
                stopwatch.Stop();
                times.Add(stopwatch.Elapsed.TotalMilliseconds);
                Assert.True(page.Items.Count <= 100);
            }
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true)
            - allocatedBefore;
        output.WriteLine(
            $"maturity queue documents=10000, calls={times.Count}, "
            + $"allocated={allocated}");
        return times;
    }

    private static async Task<string[]> ReadPlanAsync(
        VaultApplicationFixture fixture,
        string sql,
        bool includeDocumentParameters) =>
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText = "EXPLAIN QUERY PLAN\n" + sql;
                AddParameters(command.Command, includeDocumentParameters);
                var lines = new List<string>();
                await using var reader =
                    await command.Command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    lines.Add(reader.GetString(3));
                }
                return lines.ToArray();
            });

    private static async Task<long> ReadFacetScanStepsAsync(
        VaultApplicationFixture fixture) =>
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText = KnowledgeViewStore.FacetSql;
                AddParameters(command.Command, includeDocumentParameters: false);
                long steps;
                await using (var reader =
                             await command.Command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                    }
                    steps = SQLitePCL.raw.sqlite3_stmt_status(
                        reader.Handle,
                        1,
                        0);
                }
                return steps;
            });

    private static async Task<long> ReadDocumentScanStepsAsync(
        VaultApplicationFixture fixture) =>
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    KnowledgeViewStore.BuildDocumentSql(hasCursor: false);
                AddParameters(command.Command, includeDocumentParameters: true);
                long steps;
                await using (var reader =
                             await command.Command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                    }
                    steps = SQLitePCL.raw.sqlite3_stmt_status(
                        reader.Handle,
                        1,
                        0);
                }
                return steps;
            });

    private static async Task<string[]> ReadMaturityQueuePlanAsync(
        VaultApplicationFixture fixture) =>
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    "EXPLAIN QUERY PLAN\n"
                    + KnowledgeMaturityQueueStore.QueueSql;
                AddMaturityQueueParameters(command.Command);
                var lines = new List<string>();
                await using var reader =
                    await command.Command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    lines.Add(reader.GetString(3));
                }
                return lines.ToArray();
            });

    private static async Task<long> ReadMaturityQueueScanStepsAsync(
        VaultApplicationFixture fixture) =>
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    KnowledgeMaturityQueueStore.QueueSql;
                AddMaturityQueueParameters(command.Command);
                long steps;
                await using (var reader =
                             await command.Command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                    }
                    steps = SQLitePCL.raw.sqlite3_stmt_status(
                        reader.Handle,
                        1,
                        0);
                }
                return steps;
            });

    private static void AddParameters(
        Microsoft.Data.Sqlite.SqliteCommand command,
        bool includeDocumentParameters)
    {
        command.Parameters.AddWithValue("$field", "project");
        command.Parameters.AddWithValue("$activeOnly", 1L);
        if (includeDocumentParameters)
        {
            command.Parameters.AddWithValue("$facetKey", "PROJECT 001");
            command.Parameters.AddWithValue("$unset", 0L);
            command.Parameters.AddWithValue("$limit", 101);
        }
    }

    private static void AddMaturityQueueParameters(
        Microsoft.Data.Sqlite.SqliteCommand command)
    {
        command.Parameters.AddWithValue(
            "$bucket",
            "pending_organization");
        command.Parameters.AddWithValue("$afterUpdatedAt", DBNull.Value);
        command.Parameters.AddWithValue("$afterDocumentId", DBNull.Value);
        command.Parameters.AddWithValue("$limit", 101);
    }

    private static async Task InsertDocumentsAsync(
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
                        printf('View Perf %05d', value),
                        printf('VIEW PERF %05d', value),
                        printf('views/%05d.md', value),
                        printf('views/%05d.md', value),
                        'md',
                        'body',
                        json_object(
                            'project', printf('Project %03d', value % 300),
                            'type', CASE value % 3
                                WHEN 0 THEN 'technical-note'
                                WHEN 1 THEN 'experience'
                                ELSE 'plan'
                            END,
                            'maturity', CASE value % 4
                                WHEN 0 THEN 'raw'
                                WHEN 1 THEN 'organized'
                                WHEN 2 THEN 'verified'
                                ELSE 'practiced'
                            END,
                            'status', CASE WHEN value % 4 = 0
                                THEN 'planned'
                                ELSE 'active'
                            END
                        ),
                        '[]',
                        1,
                        '2026-11-04T00:00:00Z',
                        printf('2026-11-04T%02d:%02d:00Z',
                               (value / 60) % 24,
                               value % 60),
                        NULL,
                        NULL
                    FROM seq;
                    """;
                command.Command.Parameters.AddWithValue("$start", start);
                command.Command.Parameters.AddWithValue("$end", end);
                return await command.Command.ExecuteNonQueryAsync();
            });
    }

    private static double Percentile(
        IReadOnlyList<double> values,
        double percentile)
    {
        var ordered = values.Order().ToArray();
        var index = (int)Math.Ceiling((ordered.Length - 1) * percentile);
        return ordered[index];
    }
}
