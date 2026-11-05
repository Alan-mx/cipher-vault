using System.Diagnostics;
using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Application.Tests;

public sealed class KnowledgeTriageServiceTests
{
    private const string NoteTypeId =
        "00000000-0000-4000-8000-000000001001";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task QueryCombinesMissingPropertyAndMissingSource()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        await AddRequiredAuthorAsync(fixture);
        var knowledgeId = await InsertKnowledgeAsync(
            fixture,
            "Missing metadata",
            "{}",
            DateTimeOffset.UtcNow.AddDays(-1));
        var service = new KnowledgeTriageService(
            fixture.Manager,
            fixture.TimeProvider);

        var page = await service.GetAsync(new KnowledgeTriageQuery(
            null,
            null,
            30,
            null,
            20,
            null));

        var item = Assert.Single(page.Items);
        Assert.Equal(knowledgeId, item.KnowledgeId);
        Assert.Equal(
            [
                KnowledgeTriageReason.MissingRequiredProperty,
                KnowledgeTriageReason.MissingSource
            ],
            item.ReasonCodes);
        Assert.Equal(["author"], item.MissingPropertyKeys);
        Assert.Equal(0, item.SourceReferenceCount);
        Assert.Equal(1, page.Counts.MissingRequiredProperty);
        Assert.Equal(1, page.Counts.MissingSource);
    }

    [Fact]
    public async Task QueryDetectsStaleOnlyWhenSourceIsPresent()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var source = await CreateSourceAsync(drafts, user);
        var knowledge = await CreateKnowledgeWithSourceAsync(
            drafts,
            user,
            source);
        await fixture.QueryAsync(async context =>
        {
            await context.BeginTransactionAsync(CancellationToken.None);
            using var command = context.CreateCommand();
            command.Command.CommandText =
                """
                UPDATE knowledge_items
                SET created_at = $createdAt,
                    updated_at = $updatedAt
                WHERE knowledge_id = $id;
                """;
            command.Command.Parameters.AddWithValue(
                "$updatedAt",
                DateTimeOffset.UtcNow.AddDays(-40).ToString("O"));
            command.Command.Parameters.AddWithValue(
                "$createdAt",
                DateTimeOffset.UtcNow.AddDays(-41).ToString("O"));
            command.Command.Parameters.AddWithValue(
                "$id",
                knowledge.ObjectId.ToString("D"));
            await command.Command.ExecuteNonQueryAsync();
            await context.CommitTransactionAsync(CancellationToken.None);
            return 0;
        });
        var service = new KnowledgeTriageService(
            fixture.Manager,
            fixture.TimeProvider);

        var page = await service.GetAsync(new KnowledgeTriageQuery(
            null,
            null,
            30,
            null,
            20,
            null));

        var item = Assert.Single(page.Items);
        Assert.Equal(
            [KnowledgeTriageReason.Stale],
            item.ReasonCodes);
        Assert.Equal(1, item.SourceReferenceCount);
        Assert.Empty(item.MissingPropertyKeys);
        Assert.Equal(1, page.Counts.Stale);
        Assert.Equal(1, page.Counts.TotalDistinct);
    }

    [Fact]
    public async Task QueryRecomputesWhenModelRequiredPropertyChanges()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        await InsertKnowledgeAsync(
            fixture,
            "Dynamic required property",
            "{}",
            DateTimeOffset.UtcNow);
        var service = new KnowledgeTriageService(
            fixture.Manager,
            fixture.TimeProvider);
        var before = await service.GetAsync(new KnowledgeTriageQuery(
            KnowledgeTriageReason.MissingRequiredProperty,
            null,
            30,
            null,
            20,
            null));
        Assert.Empty(before.Items);

        await AddRequiredAuthorAsync(fixture);
        var after = await service.GetAsync(new KnowledgeTriageQuery(
            KnowledgeTriageReason.MissingRequiredProperty,
            null,
            30,
            null,
            20,
            null));

        var item = Assert.Single(after.Items);
        Assert.Equal(["author"], item.MissingPropertyKeys);
    }

    [Fact]
    public async Task QueryCursorKeepsPriorityOrderAndStableCutoff()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        await AddRequiredAuthorAsync(fixture);
        var missingProperty = await InsertKnowledgeAsync(
            fixture,
            "A missing property",
            "{}",
            DateTimeOffset.UtcNow.AddDays(-40));
        var missingSource = await InsertKnowledgeAsync(
            fixture,
            "B missing source",
            "{\"author\":\"Ada\"}",
            DateTimeOffset.UtcNow.AddDays(-1));
        var service = new KnowledgeTriageService(
            fixture.Manager,
            fixture.TimeProvider);

        var first = await service.GetAsync(new KnowledgeTriageQuery(
            null,
            null,
            30,
            null,
            1,
            null));
        Assert.Single(first.Items);
        Assert.Equal(missingProperty, first.Items[0].KnowledgeId);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        var second = await service.GetAsync(new KnowledgeTriageQuery(
            null,
            null,
            30,
            null,
            1,
            first.NextCursor));
        Assert.Single(second.Items);
        Assert.Equal(missingSource, second.Items[0].KnowledgeId);
        Assert.NotEqual(
            first.Items[0].KnowledgeId,
            second.Items[0].KnowledgeId);
    }

    [Fact]
    public async Task QuerySearchUsesApprovedKnowledgeFts()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var knowledge = await CreateKnowledgeAsync(
            drafts,
            user,
            "Distinctive triage search marker");
        var service = new KnowledgeTriageService(
            fixture.Manager,
            fixture.TimeProvider);

        var page = await service.GetAsync(new KnowledgeTriageQuery(
            null,
            null,
            30,
            "Distinctive",
            20,
            null));

        var item = Assert.Single(page.Items);
        Assert.Equal(knowledge.ObjectId, item.KnowledgeId);
    }

    [Fact]
    public async Task QueryMeetsTenThousandKnowledgeLatencyTarget()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        await AddRequiredAuthorAsync(fixture);
        await InsertKnowledgeBatchAsync(fixture, 10_000);
        var service = new KnowledgeTriageService(
            fixture.Manager,
            fixture.TimeProvider);
        var query = new KnowledgeTriageQuery(
            null,
            null,
            30,
            null,
            20,
            null);

        _ = await service.GetAsync(query);
        var timings = new List<double>();
        for (var index = 0; index < 5; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            var page = await service.GetAsync(query);
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
            Assert.Equal(20, page.Items.Count);
        }

        var p95 = timings.Max();
        Assert.True(
            p95 <= 3_000,
            $"Triage P95 was {p95:F1} ms on 10,000 active Knowledge.");
    }

    private static async Task AddRequiredAuthorAsync(
        VaultApplicationFixture fixture)
    {
        await fixture.QueryAsync(async context =>
        {
            await context.BeginTransactionAsync(CancellationToken.None);
            using var command = context.CreateCommand();
            command.Command.CommandText =
                """
                INSERT INTO knowledge_property_definitions(
                    property_id, knowledge_type_id, property_key, name,
                    value_type, is_required, default_value_json,
                    constraints_json, is_enabled, sort_order,
                    created_at, updated_at)
                VALUES(
                    $id, $typeId, 'author', 'Author',
                    'string', 1, NULL, '{}', 1, 10, $now, $now);
                """;
            command.Command.Parameters.AddWithValue(
                "$id",
                Guid.NewGuid().ToString("D"));
            command.Command.Parameters.AddWithValue("$typeId", NoteTypeId);
            command.Command.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToString("O"));
            await command.Command.ExecuteNonQueryAsync();
            await context.CommitTransactionAsync(CancellationToken.None);
            return 0;
        });
    }

    private static Task<Guid> InsertKnowledgeAsync(
        VaultApplicationFixture fixture,
        string title,
        string propertiesJson,
        DateTimeOffset updatedAt) =>
        fixture.QueryAsync(async context =>
        {
            await context.BeginTransactionAsync(CancellationToken.None);
            var knowledgeId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow.ToString("O");
            var createdAt = updatedAt.AddDays(-1).ToString("O");
            using (var item = context.CreateCommand())
            {
                item.Command.CommandText =
                    """
                    INSERT INTO knowledge_items(
                        knowledge_id, title, title_key, summary,
                        knowledge_type_id, status, current_revision_id,
                        created_at, updated_at)
                    VALUES(
                        $id, $title, $titleKey, '',
                        $typeId, 'active', $revisionId, $createdAt, $updatedAt);
                    """;
                item.Command.Parameters.AddWithValue(
                    "$id",
                    knowledgeId.ToString("D"));
                item.Command.Parameters.AddWithValue("$title", title);
                item.Command.Parameters.AddWithValue(
                    "$titleKey",
                    title.ToUpperInvariant());
                item.Command.Parameters.AddWithValue("$typeId", NoteTypeId);
                item.Command.Parameters.AddWithValue(
                    "$revisionId",
                    revisionId.ToString("D"));
                item.Command.Parameters.AddWithValue("$now", now);
                item.Command.Parameters.AddWithValue(
                    "$createdAt",
                    createdAt);
                item.Command.Parameters.AddWithValue(
                    "$updatedAt",
                    updatedAt.ToString("O"));
                await item.Command.ExecuteNonQueryAsync();
            }
            using (var revision = context.CreateCommand())
            {
                revision.Command.CommandText =
                    """
                    INSERT INTO knowledge_revisions(
                        knowledge_revision_id, knowledge_id, revision,
                        knowledge_type_id, template_id,
                        knowledge_model_version, title, summary,
                        content_markdown, content_hash, properties_json,
                        tags_json, created_at)
                    VALUES(
                        $revisionId, $id, 1, $typeId, NULL,
                        1, $title, '', '# Body', $hash, $properties,
                        '[]', $createdAt);
                    """;
                revision.Command.Parameters.AddWithValue(
                    "$revisionId",
                    revisionId.ToString("D"));
                revision.Command.Parameters.AddWithValue(
                    "$id",
                    knowledgeId.ToString("D"));
                revision.Command.Parameters.AddWithValue("$typeId", NoteTypeId);
                revision.Command.Parameters.AddWithValue("$title", title);
                revision.Command.Parameters.AddWithValue(
                    "$hash",
                    new string('0', 64));
                revision.Command.Parameters.AddWithValue(
                    "$properties",
                    propertiesJson);
                revision.Command.Parameters.AddWithValue("$now", now);
                revision.Command.Parameters.AddWithValue(
                    "$createdAt",
                    createdAt);
                await revision.Command.ExecuteNonQueryAsync();
            }
            await context.CommitTransactionAsync(CancellationToken.None);
            return knowledgeId;
        });

    private static async Task InsertKnowledgeBatchAsync(
        VaultApplicationFixture fixture,
        int count)
    {
        await fixture.QueryAsync(async context =>
        {
            await context.BeginTransactionAsync(CancellationToken.None);
            const string sourceId = "30000000-0000-4000-8000-000000000001";
            const string sourceRevisionId =
                "30000000-0000-4000-8000-000000000002";
            const string sourceLocatorId =
                "30000000-0000-4000-8000-000000000003";
            using (var source = context.CreateCommand())
            {
                source.Command.CommandText =
                    """
                    INSERT INTO sources(
                        source_id, source_kind, title, title_key, metadata_json,
                        status, current_revision_id, created_at, updated_at)
                    VALUES(
                        $sourceId, 'web_page', 'Batch source',
                        'BATCH SOURCE', '{"url":"https://example.com/batch"}',
                        'active', $revisionId,
                        '2020-01-01T00:00:00+00:00',
                        '2020-01-01T00:00:00+00:00');
                    INSERT INTO source_revisions(
                        source_revision_id, source_id, revision, source_kind,
                        title, metadata_json, content_format, content_text,
                        content_hash, knowledge_model_version,
                        locator_index_version, created_at)
                    VALUES(
                        $revisionId, $sourceId, 1, 'web_page', 'Batch source',
                        '{"url":"https://example.com/batch"}', 'markdown',
                        '# Section\n\nClaim text.', $hash, 1, 1,
                        '2020-01-01T00:00:00+00:00');
                    INSERT INTO source_locators(
                        source_locator_id, source_revision_id, locator_type,
                        locator_key, locator_value_json, label,
                        start_offset, end_offset, ordinal, parent_locator_id)
                    VALUES(
                        $locatorId, $revisionId, 'section',
                        '["Section"]', '["Section"]', 'Section',
                        0, 10, 0, NULL);
                    """;
                source.Command.Parameters.AddWithValue("$sourceId", sourceId);
                source.Command.Parameters.AddWithValue(
                    "$revisionId",
                    sourceRevisionId);
                source.Command.Parameters.AddWithValue(
                    "$locatorId",
                    sourceLocatorId);
                source.Command.Parameters.AddWithValue(
                    "$hash",
                    new string('0', 64));
                await source.Command.ExecuteNonQueryAsync();
            }
            using (var items = context.CreateCommand())
            {
                items.Command.CommandText =
                    """
                    WITH RECURSIVE seq(n) AS (
                        SELECT 1
                        UNION ALL
                        SELECT n + 1 FROM seq WHERE n < $count
                    )
                    INSERT INTO knowledge_items(
                        knowledge_id, title, title_key, summary,
                        knowledge_type_id, status, current_revision_id,
                        created_at, updated_at)
                    SELECT
                        printf('10000000-0000-4000-8000-%012d', n),
                        'Knowledge ' || n,
                        'KNOWLEDGE ' || n,
                        '',
                        $typeId,
                        'active',
                        printf('20000000-0000-4000-8000-%012d', n),
                        '2020-01-01T00:00:00+00:00',
                        strftime(
                            '%Y-%m-%dT%H:%M:%f+00:00',
                            $now,
                            printf('-%d days', n % 120)
                        )
                    FROM seq;
                    """;
                items.Command.Parameters.AddWithValue("$count", count);
                items.Command.Parameters.AddWithValue("$typeId", NoteTypeId);
                items.Command.Parameters.AddWithValue(
                    "$now",
                    DateTimeOffset.UtcNow.ToString("O"));
                await items.Command.ExecuteNonQueryAsync();
            }
            using (var revisions = context.CreateCommand())
            {
                revisions.Command.CommandText =
                    """
                    WITH RECURSIVE seq(n) AS (
                        SELECT 1
                        UNION ALL
                        SELECT n + 1 FROM seq WHERE n < $count
                    )
                    INSERT INTO knowledge_revisions(
                        knowledge_revision_id, knowledge_id, revision,
                        knowledge_type_id, template_id,
                        knowledge_model_version, title, summary,
                        content_markdown, content_hash, properties_json,
                        tags_json, created_at)
                    SELECT
                        printf('20000000-0000-4000-8000-%012d', n),
                        printf('10000000-0000-4000-8000-%012d', n),
                        1,
                        $typeId,
                        NULL,
                        1,
                        'Knowledge ' || n,
                        '',
                        '# Body',
                        $hash,
                        CASE WHEN n % 5 = 0
                             THEN '{}'
                             ELSE '{"author":"Ada"}'
                        END,
                        '[]',
                        '2020-01-01T00:00:00+00:00'
                    FROM seq;
                    """;
                revisions.Command.Parameters.AddWithValue("$count", count);
                revisions.Command.Parameters.AddWithValue("$typeId", NoteTypeId);
                revisions.Command.Parameters.AddWithValue(
                    "$hash",
                    new string('0', 64));
                revisions.Command.Parameters.AddWithValue(
                    "$now",
                    DateTimeOffset.UtcNow.ToString("O"));
                await revisions.Command.ExecuteNonQueryAsync();
            }
            using (var references = context.CreateCommand())
            {
                references.Command.CommandText =
                    """
                    WITH RECURSIVE seq(n) AS (
                        SELECT 1
                        UNION ALL
                        SELECT n + 1 FROM seq WHERE n < $count
                    )
                    INSERT INTO source_references(
                        source_reference_id, knowledge_revision_id, source_id,
                        source_revision_id, source_revision, source_locator_id,
                        locator_type, locator_key, locator_value_json, excerpt,
                        extraction_metadata_json, created_at)
                    SELECT
                        printf('40000000-0000-4000-8000-%012d', n),
                        printf('20000000-0000-4000-8000-%012d', n),
                        $sourceId,
                        $sourceRevisionId,
                        1,
                        $locatorId,
                        'section',
                        '["Section"]',
                        '["Section"]',
                        'Claim text.',
                        '{}',
                        '2020-01-01T00:00:00+00:00'
                    FROM seq
                    WHERE n % 3 <> 0;
                    """;
                references.Command.Parameters.AddWithValue("$count", count);
                references.Command.Parameters.AddWithValue(
                    "$sourceId",
                    sourceId);
                references.Command.Parameters.AddWithValue(
                    "$sourceRevisionId",
                    sourceRevisionId);
                references.Command.Parameters.AddWithValue(
                    "$locatorId",
                    sourceLocatorId);
                await references.Command.ExecuteNonQueryAsync();
            }
            await context.CommitTransactionAsync(CancellationToken.None);
            return 0;
        });
    }

    private static async Task<(Guid ObjectId, int ObjectRevision)> CreateSourceAsync(
        UnifiedDraftService drafts,
        VaultActorContext user)
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                sourceKind = "web_page",
                title = "Triage source",
                metadata = new { url = "https://example.com/triage" },
                contentFormat = "markdown",
                contentText = "# Section\n\nClaim text."
            },
            JsonOptions);
        var created = await drafts.CreateAsync(
            new CreateDraftInput(
                DraftTargetObjectType.Source,
                DraftOperationType.Create,
                null,
                null,
                payload),
            user,
            Guid.NewGuid());
        var submitted = await drafts.SubmitAsync(
            created.DraftId,
            created.DraftRevision,
            user,
            Guid.NewGuid());
        var applied = await drafts.ApplyAsync(
            submitted.DraftId,
            submitted.DraftRevision,
            user,
            Guid.NewGuid());
        return (applied.ObjectId, applied.ObjectRevision);
    }

    private static async Task<(Guid ObjectId, int ObjectRevision)>
        CreateKnowledgeWithSourceAsync(
            UnifiedDraftService drafts,
            VaultActorContext user,
            (Guid ObjectId, int ObjectRevision) source)
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                title = "Triage knowledge",
                summary = "",
                knowledgeTypeId = Guid.Parse(NoteTypeId),
                templateId = (string?)null,
                contentMarkdown = "# Triage knowledge",
                properties = new { },
                tags = Array.Empty<string>(),
                sourceReferences = new[]
                {
                    new
                    {
                        sourceId = source.ObjectId,
                        sourceRevision = source.ObjectRevision,
                        locatorType = "section",
                        locatorValue = new[] { "Section" },
                        excerpt = "Claim text.",
                        extractionMetadata = new { method = "test" }
                    }
                }
            },
            JsonOptions);
        var created = await drafts.CreateAsync(
            new CreateDraftInput(
                DraftTargetObjectType.Knowledge,
                DraftOperationType.Create,
                null,
                null,
                payload),
            user,
            Guid.NewGuid());
        var submitted = await drafts.SubmitAsync(
            created.DraftId,
            created.DraftRevision,
            user,
            Guid.NewGuid());
        var applied = await drafts.ApplyAsync(
            submitted.DraftId,
            submitted.DraftRevision,
            user,
            Guid.NewGuid());
        return (applied.ObjectId, applied.ObjectRevision);
    }

    private static async Task<(Guid ObjectId, int ObjectRevision)>
        CreateKnowledgeAsync(
            UnifiedDraftService drafts,
            VaultActorContext user,
            string title)
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                title,
                summary = "",
                knowledgeTypeId = Guid.Parse(NoteTypeId),
                templateId = (string?)null,
                contentMarkdown = "# " + title,
                properties = new { },
                tags = Array.Empty<string>(),
                sourceReferences = Array.Empty<object>()
            },
            JsonOptions);
        var created = await drafts.CreateAsync(
            new CreateDraftInput(
                DraftTargetObjectType.Knowledge,
                DraftOperationType.Create,
                null,
                null,
                payload),
            user,
            Guid.NewGuid());
        var submitted = await drafts.SubmitAsync(
            created.DraftId,
            created.DraftRevision,
            user,
            Guid.NewGuid());
        var applied = await drafts.ApplyAsync(
            submitted.DraftId,
            submitted.DraftRevision,
            user,
            Guid.NewGuid());
        return (applied.ObjectId, applied.ObjectRevision);
    }
}
