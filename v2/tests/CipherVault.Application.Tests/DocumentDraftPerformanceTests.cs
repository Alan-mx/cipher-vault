using System.Diagnostics;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Storage.Drafts;
using Xunit.Abstractions;

namespace CipherVault.Application.Tests;

public sealed class DocumentDraftPerformanceTests
{
    private readonly ITestOutputHelper output;

    public DocumentDraftPerformanceTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task TenThousandDraftsKeepSummaryAndHistoryFiltersBounded()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var summary = new DocumentDraftQueueSummary(0, 0, 0);
        var itemCount = 0;
        var summaryElapsed = TimeSpan.Zero;
        var listElapsed = TimeSpan.Zero;
        var plan = string.Empty;

        _ = await fixture.QueryAsync(
            async context =>
            {
                using (var seed = context.CreateCommand())
                {
                    seed.Command.CommandText =
                        """
                        WITH RECURSIVE seq(value) AS (
                            SELECT 1
                            UNION ALL
                            SELECT value + 1 FROM seq WHERE value < 10000
                        )
                        INSERT INTO document_drafts(
                            draft_id,
                            draft_revision,
                            draft_type,
                            status,
                            target_document_id,
                            base_document_revision,
                            payload_version,
                            payload_json,
                            base_snapshot_json,
                            source_actor,
                            created_at,
                            updated_at,
                            terminal_at,
                            applied_document_id,
                            applied_document_revision,
                            applied_at)
                        SELECT
                            printf(
                                '%08x-0000-4000-8000-%012x',
                                value,
                                value
                            ),
                            1,
                            'create',
                            CASE
                                WHEN value % 20 BETWEEN 1 AND 7
                                    THEN 'pending_review'
                                WHEN value % 20 BETWEEN 8 AND 11
                                    THEN 'draft'
                                WHEN value % 20 BETWEEN 12 AND 13
                                    THEN 'conflict'
                                WHEN value % 20 BETWEEN 14 AND 16
                                    THEN 'rejected'
                                ELSE 'discarded'
                            END,
                            NULL,
                            NULL,
                            1,
                            '{}',
                            NULL,
                            CASE WHEN value % 2 = 0 THEN 'mcp' ELSE 'user_ui' END,
                            printf(
                                '2026-10-%02dT00:00:00.0000000+00:00',
                                1 + (value % 28)
                            ),
                            printf(
                                '2026-10-%02dT00:00:00.0000000+00:00',
                                1 + (value % 28)
                            ),
                            CASE
                                WHEN value % 20 BETWEEN 14 AND 19
                                    OR value % 20 = 0
                                    THEN printf(
                                        '2026-10-%02dT00:00:00.0000000+00:00',
                                        1 + (value % 28)
                                    )
                                ELSE NULL
                            END,
                            NULL,
                            NULL,
                            NULL
                        FROM seq;
                        """;
                    await seed.Command.ExecuteNonQueryAsync();
                }

                var stopwatch = Stopwatch.StartNew();
                summary = await DocumentDraftStore.GetSummaryAsync(
                    context,
                    CancellationToken.None);
                summaryElapsed = stopwatch.Elapsed;

                var query = new DocumentDraftQuery(
                    DocumentDraftQueue.History,
                    DocumentDraftStatus.Rejected,
                    DocumentDraftType.Create,
                    VaultActorKind.Mcp,
                    new DateTimeOffset(
                        2026,
                        10,
                        2,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                    new DateTimeOffset(
                        2026,
                        10,
                        29,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                    null,
                    new CursorPageRequest(50, null));
                stopwatch.Restart();
                var page = await DocumentDraftStore.ListAsync(
                    context,
                    query,
                    new DateTimeOffset(
                        2026,
                        11,
                        1,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                    CancellationToken.None);
                listElapsed = stopwatch.Elapsed;
                itemCount = page.Items.Count;
                Assert.NotNull(page.NextCursor);

                using var explain = context.CreateCommand();
                explain.Command.CommandText =
                    """
                    EXPLAIN QUERY PLAN
                    SELECT draft_id
                    FROM document_drafts
                    WHERE updated_at <= '2026-11-01T00:00:00.0000000+00:00'
                      AND status = 'rejected'
                      AND draft_type = 'create'
                      AND source_actor = 'mcp'
                      AND updated_at >= '2026-10-02T00:00:00.0000000+00:00'
                      AND updated_at < '2026-10-29T00:00:00.0000000+00:00'
                    ORDER BY updated_at DESC, draft_id DESC
                    LIMIT 51;
                    """;
                await using var reader =
                    await explain.Command.ExecuteReaderAsync();
                var planLines = new List<string>();
                while (await reader.ReadAsync())
                {
                    planLines.Add(reader.GetString(3));
                }
                plan = string.Join(" | ", planLines);
                return 1;
            });

        Assert.Equal(new DocumentDraftQueueSummary(3_500, 3_000, 3_500), summary);
        Assert.Equal(50, itemCount);
        Assert.Contains(
            "ix_document_drafts_status",
            plan,
            StringComparison.Ordinal);
        output.WriteLine(
            $"10,000 drafts summary={summaryElapsed.TotalMilliseconds:F1} ms; " +
            $"filtered history page={listElapsed.TotalMilliseconds:F1} ms; " +
            $"plan={plan}");
        Assert.True(
            summaryElapsed < TimeSpan.FromSeconds(5),
            $"Summary took {summaryElapsed}.");
        Assert.True(
            listElapsed < TimeSpan.FromSeconds(5),
            $"Filtered history list took {listElapsed}.");
    }

    [Fact]
    public async Task NearLimitDraftDetailComputesWithinBoundedTime()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Large target",
                "notes/large-target.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Large target\n",
                []),
            user);
        const int operationCount = 31;
        const int operationBytes = 120_000;
        var operations = Enumerable.Range(0, operationCount)
            .Select(_ => new
            {
                type = "replace_body",
                content = new string('x', operationBytes)
            })
            .ToArray();
        using var payloadDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                operations,
                reviewContext = new
                {
                    summary = "Large payload",
                    sources = Array.Empty<object>()
                }
            }));
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Update,
                document.DocumentId,
                document.Revision,
                payloadDocument.RootElement.Clone()),
            mcp,
            Guid.NewGuid());

        var stopwatch = Stopwatch.StartNew();
        var detail = await fixture.Drafts.GetAsync(created.DraftId);
        stopwatch.Stop();

        Assert.True(detail.Preview.Content.Length >= operationBytes);
        Assert.True(detail.Payload.GetRawText().Length >= 3_700_000);
        Assert.True(detail.Preview.StructuredChanges.Changes.Count > 0);
        output.WriteLine(
            $"near-4-MiB detail={stopwatch.Elapsed.TotalMilliseconds:F1} ms; " +
            $"payloadChars={detail.Payload.GetRawText().Length}");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Large detail took {stopwatch.Elapsed}.");
    }
}
