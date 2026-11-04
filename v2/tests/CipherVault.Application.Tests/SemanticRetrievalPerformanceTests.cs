using System.Diagnostics;
using CipherVault.Application.Semantic;
using CipherVault.Application.Search;
using CipherVault.Core.Documents;
using CipherVault.Core.Search;
using CipherVault.Core.Semantic;
using CipherVault.Core.Storage.Semantic;
using Xunit.Abstractions;

namespace CipherVault.Application.Tests;

public sealed class SemanticRetrievalPerformanceTests
{
    private const int DocumentCount = 10_000;
    private const int Dimension = 768;
    private readonly ITestOutputHelper output;

    public SemanticRetrievalPerformanceTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task ScaleAndStressMeasurementsAreBounded()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var provider = new PerformanceProvider();
        var index = new SemanticIndexService(
            fixture.Manager,
            provider,
            1,
            remoteDeploymentAllowed: false,
            endpointDisplay: "http://127.0.0.1:1234/v1",
            fixture.TimeProvider);
        await index.EnsureProfileAsync();
        var status = await index.GetStatusAsync(
            new VaultActorContext(VaultActorKind.UserUi));
        var profileId = status.Profile!.ProfileId;
        await SetReadyAsync(fixture, profileId);

        await InsertDocumentsAsync(fixture);
        await InsertChunksAsync(fixture, profileId, firstOrdinal: 0);
        var fiftyThousand = await MeasureVectorQueriesAsync(
            fixture,
            profileId,
            repetitions: 5);
        output.WriteLine(
            $"50k chunks: p50={fiftyThousand.P50:F1}ms "
            + $"p95={fiftyThousand.P95:F1}ms "
            + $"peakAllocated={fiftyThousand.AllocatedBytes}");

        await InsertChunksAsync(fixture, profileId, firstOrdinal: 5);
        var hundredThousand = await MeasureVectorQueriesAsync(
            fixture,
            profileId,
            repetitions: 3);
        var fts = await MeasureFtsAsync(fixture);
        var hybrid = MeasureHybridRanking();
        var embedding = MeasureLocalEmbeddingPreparation();
        var databaseBytes = new FileInfo(
            Path.Combine(fixture.VaultDirectory, "vault.db")).Length;
        var peakWorkingSetBytes = Process
            .GetCurrentProcess()
            .PeakWorkingSet64;
        output.WriteLine(
            $"100k chunks: p50={hundredThousand.P50:F1}ms "
            + $"p95={hundredThousand.P95:F1}ms "
            + $"peakAllocated={hundredThousand.AllocatedBytes}");
        output.WriteLine($"FTS: {fts:F1}ms");
        output.WriteLine($"hybrid ranking: {hybrid:F3}ms");
        output.WriteLine($"chunk + vector preparation: {embedding:F1}ms");
        output.WriteLine($"database bytes={databaseBytes}");
        output.WriteLine($"peak working set bytes={peakWorkingSetBytes}");

        Assert.Equal(
            100_000,
            await CountChunksAsync(fixture, profileId));
        Assert.True(
            fiftyThousand.P95 < TimeSpan.FromSeconds(30).TotalMilliseconds,
            $"50k vector query P95 was {fiftyThousand.P95:F1}ms.");
        Assert.True(
            hundredThousand.P95 < TimeSpan.FromSeconds(45).TotalMilliseconds,
            $"100k vector query P95 was {hundredThousand.P95:F1}ms.");
        Assert.True(
            fts < TimeSpan.FromSeconds(10).TotalMilliseconds,
            $"FTS query took {fts:F1}ms.");
    }

    private static async Task<Measurement> MeasureVectorQueriesAsync(
        VaultApplicationFixture fixture,
        string profileId,
        int repetitions)
    {
        var vector = new float[Dimension];
        vector[0] = 1f;
        var query = new SearchQuery(
            "performance",
            SearchMode.Semantic,
            null,
            null,
            null,
            null,
            null,
            false,
            new CursorPageRequest(20));
        var elapsed = new List<double>(repetitions);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        foreach (var repetition in Enumerable.Range(0, repetitions))
        {
            var stopwatch = Stopwatch.StartNew();
            _ = await fixture.QueryAsync(
                async context =>
                {
                    var profile = await SemanticProfileStore.GetActiveAsync(
                        context,
                        CancellationToken.None);
                    Assert.NotNull(profile);
                    Assert.Equal(profileId, profile.ProfileId);
                    return await SemanticSearchStore.SearchAsync(
                        context,
                        profile,
                        query,
                        vector,
                        HybridSearchRanker.CandidateLimit,
                        fixture.TimeProvider.GetUtcNow(),
                        CancellationToken.None);
                });
            stopwatch.Stop();
            elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        return new Measurement(
            Percentile(elapsed, 0.50),
            Percentile(elapsed, 0.95),
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
    }

    private static async Task<double> MeasureFtsAsync(
        VaultApplicationFixture fixture)
    {
        var stopwatch = Stopwatch.StartNew();
        _ = await fixture.Search.SearchAsync(
            new SearchQuery(
                "body",
                SearchMode.Keyword,
                null,
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest(100)));
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double MeasureHybridRanking()
    {
        var now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
        var keywordHits = Enumerable.Range(0, HybridSearchRanker.CandidateLimit)
            .Select(index => new SearchHit(
                Guid.NewGuid(),
                $"Keyword {index}",
                $"perf/keyword-{index}.md",
                1,
                "body",
                -index)
            {
                UpdatedAt = now.AddSeconds(-index)
            })
            .ToArray();
        var semanticHits = Enumerable
            .Range(0, HybridSearchRanker.CandidateLimit)
            .Select(index => new SemanticSearchHit(
                Guid.NewGuid(),
                $"Semantic {index}",
                $"perf/semantic-{index}.md",
                1,
                1,
                0,
                ["Performance"],
                0,
                4,
                "body",
                1d - (index / 100d),
                now.AddSeconds(-index)))
            .ToArray();
        var query = new SearchQuery(
            "performance",
            SearchMode.Hybrid,
            null,
            null,
            null,
            null,
            null,
            false,
            new CursorPageRequest(20));
        var stopwatch = Stopwatch.StartNew();
        _ = HybridSearchRanker.BuildHybridPage(
            query,
            new string('a', 64),
            new SearchPage(
                keywordHits,
                new SearchCoverage(
                    DocumentCount,
                    DocumentCount,
                    keywordHits.Length,
                    keywordHits.Length,
                    false),
                null),
            new SemanticSearchResult(
                semanticHits,
                DocumentCount,
                semanticHits.Length,
                false),
            now);
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double MeasureLocalEmbeddingPreparation()
    {
        var content = string.Join(
            "\n\n",
            Enumerable.Range(0, 100).Select(index =>
                $"## Section {index}\n\n"
                + string.Concat(Enumerable.Repeat("cache consistency ", 120))));
        var stopwatch = Stopwatch.StartNew();
        var chunks = SemanticTextChunker.Chunk(
            new SemanticChunkingRequest(
                Guid.NewGuid(),
                1,
                "Performance",
                VaultDocumentFormat.Markdown,
                content,
                ["performance"],
                new Dictionary<string, string>()),
            maxInputTokens: 8_192,
            value => value.Length);
        foreach (var chunk in chunks)
        {
            var vector = new float[Dimension];
            vector[0] = chunk.ChunkOrdinal + 1;
            _ = SemanticVectorCodec.NormalizeAndEncode(vector);
        }
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Percentile(
        IReadOnlyList<double> values,
        double percentile)
    {
        var ordered = values.Order().ToArray();
        var index = (int)Math.Ceiling((ordered.Length - 1) * percentile);
        return ordered[index];
    }

    private static async Task SetReadyAsync(
        VaultApplicationFixture fixture,
        string profileId) =>
        _ = await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.UpdateStatusAsync(
                    context,
                    profileId,
                    SemanticProfileStatus.Ready,
                    null,
                    fixture.TimeProvider.GetUtcNow(),
                    CancellationToken.None);
                return 1;
            });

    private static async Task InsertDocumentsAsync(
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
                        printf('Semantic Perf %05d', value),
                        printf('SEMANTIC PERF %05d', value),
                        printf('semantic/perf-%05d.md', value),
                        printf('semantic/perf-%05d.md', value),
                        'md',
                        'body',
                        json_object('project', 'Performance'),
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

    private static async Task InsertChunksAsync(
        VaultApplicationFixture fixture,
        string profileId,
        int firstOrdinal) =>
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    WITH ordinals(value) AS (
                        VALUES(0), (1), (2), (3), (4)
                    )
                    INSERT INTO semantic_chunks(
                        document_id, profile_id, source_revision,
                        chunk_ordinal, heading_path_json, start_offset,
                        end_offset, chunk_content_hash, dimension,
                        vector_blob, created_at)
                    SELECT
                        d.document_id,
                        $profileId,
                        1,
                        $firstOrdinal + ordinals.value,
                        '["Performance"]',
                        0,
                        4,
                        printf('%064d', 0),
                        768,
                        zeroblob(3072),
                        '2026-11-05T00:00:00Z'
                    FROM documents AS d
                    CROSS JOIN ordinals
                    ORDER BY d.document_id, ordinals.value;
                    """;
                command.Command.Parameters.AddWithValue(
                    "$profileId",
                    profileId);
                command.Command.Parameters.AddWithValue(
                    "$firstOrdinal",
                    firstOrdinal);
                return await command.Command.ExecuteNonQueryAsync();
            });

    private static async Task<int> CountChunksAsync(
        VaultApplicationFixture fixture,
        string profileId) =>
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT count(*)
                    FROM semantic_chunks
                    WHERE profile_id = $profileId;
                    """;
                command.Command.Parameters.AddWithValue(
                    "$profileId",
                    profileId);
                return Convert.ToInt32(
                    await command.Command.ExecuteScalarAsync());
            });

    private sealed record Measurement(
        double P50,
        double P95,
        long AllocatedBytes);

    private sealed class PerformanceProvider : IEmbeddingProvider
    {
        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "performance-embed",
            Dimension,
            8_192,
            new string('a', 64),
            IsRemote: false);

        public int CountInputTokens(string input) => input.Length;

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResponse(
                Descriptor.ProviderId,
                Descriptor.ModelId,
                Descriptor.Dimension,
                request.Inputs
                    .Select(_ =>
                    {
                        var vector = new float[Dimension];
                        vector[0] = 1f;
                        return vector;
                    })
                    .ToArray()));
    }
}
