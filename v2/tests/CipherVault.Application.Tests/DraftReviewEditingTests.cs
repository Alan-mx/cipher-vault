using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.Tests;

public sealed class DraftReviewEditingTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task UserUiCanEditPendingReviewButMcpCannot()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var created = await drafts.CreateAsync(
            SourceDraft("Original"),
            user,
            Guid.NewGuid());
        var pending = await drafts.SubmitAsync(
            created.DraftId,
            created.DraftRevision,
            user,
            Guid.NewGuid());

        var denied = await Assert.ThrowsAsync<VaultException>(() =>
            drafts.UpdateAsync(
                pending.DraftId,
                pending.DraftRevision,
                SourcePayload("MCP edit"),
                mcp,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.DraftStateConflict, denied.Code);

        var updated = await drafts.UpdateAsync(
            pending.DraftId,
            pending.DraftRevision,
            SourcePayload("User edit"),
            user,
            Guid.NewGuid());
        Assert.Equal(DraftStatus.Draft, updated.Status);
        Assert.Equal(pending.DraftRevision + 1, updated.DraftRevision);
        Assert.Equal(
            1,
            await CountAuditAsync(
                fixture,
                "draft.update",
                pending.DraftId));
    }

    [Fact]
    public async Task PreviewContainsSnapshotAndFieldChanges()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var created = await drafts.CreateAsync(
            SourceDraft("Original"),
            user,
            Guid.NewGuid());
        var pending = await drafts.SubmitAsync(
            created.DraftId,
            created.DraftRevision,
            user,
            Guid.NewGuid());
        var applied = await drafts.ApplyAsync(
            pending.DraftId,
            pending.DraftRevision,
            user,
            Guid.NewGuid());
        var updatePayload = SourcePayload("Updated");
        var update = await drafts.CreateAsync(
            new CreateDraftInput(
                DraftTargetObjectType.Source,
                DraftOperationType.Update,
                applied.ObjectId,
                applied.ObjectRevision,
                updatePayload),
            user,
            Guid.NewGuid());

        var detail = await drafts.GetAsync(update.DraftId);

        Assert.Equal(
            "Original",
            detail.Record.BaseSnapshot!.Value
                .GetProperty("title")
                .GetString());
        Assert.Equal(
            "markdown",
            detail.Record.BaseSnapshot.Value
                .GetProperty("contentFormat")
                .GetString());
        Assert.Contains(
            detail.Preview.Changes,
            change =>
                change.FieldPath == "title"
                && change.Kind == DraftFieldChangeKind.Modified
                && change.Before == "Original"
                && change.After == "Updated");
        Assert.Contains(
            detail.Preview.Changes,
            change =>
                change.FieldPath == "contentText"
                && change.Kind == DraftFieldChangeKind.Modified);
        Assert.DoesNotContain(
            detail.Preview.Changes,
            change =>
                change.FieldPath == "sourceReferences"
                || change.FieldPath == "evidence");
    }

    [Fact]
    public async Task RebaseWritesAuditEvent()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var created = await drafts.CreateAsync(
            SourceDraft("Original"),
            user,
            Guid.NewGuid());
        var pending = await drafts.SubmitAsync(
            created.DraftId,
            created.DraftRevision,
            user,
            Guid.NewGuid());
        var applied = await drafts.ApplyAsync(
            pending.DraftId,
            pending.DraftRevision,
            user,
            Guid.NewGuid());
        var update = await drafts.CreateAsync(
            new CreateDraftInput(
                DraftTargetObjectType.Source,
                DraftOperationType.Update,
                applied.ObjectId,
                applied.ObjectRevision,
                SourcePayload("Rebased")),
            user,
            Guid.NewGuid());

        var rebased = await drafts.RebaseAsync(
            update.DraftId,
            update.DraftRevision,
            applied.ObjectRevision,
            user,
            Guid.NewGuid());

        Assert.Equal(DraftStatus.Draft, rebased.Status);
        Assert.Equal(
            1,
            await CountAuditAsync(
                fixture,
                "draft.rebase",
                update.DraftId));
    }

    [Fact]
    public async Task IdempotencyKeyCannotBeReusedAcrossOperationsOrActors()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var key = Guid.NewGuid();
        var created = await drafts.CreateAsync(
            SourceDraft("Original"),
            user,
            key);

        var differentOperation = await Assert.ThrowsAsync<VaultException>(() =>
            drafts.SubmitAsync(
                created.DraftId,
                created.DraftRevision,
                user,
                key));
        Assert.Equal(
            VaultErrorCode.IdempotencyKeyReused,
            differentOperation.Code);

        var differentActor = await Assert.ThrowsAsync<VaultException>(() =>
            drafts.CreateAsync(
                SourceDraft("MCP"),
                mcp,
                key));
        Assert.Equal(
            VaultErrorCode.IdempotencyKeyReused,
            differentActor.Code);
    }

    [Fact]
    public void DiffMarksMissingSnapshotFieldsAsBaselineUnavailable()
    {
        var knowledgeId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                title = "Updated",
                summary = "",
                knowledgeTypeId =
                    Guid.Parse("00000000-0000-4000-8000-000000001001"),
                templateId = (string?)null,
                contentMarkdown = "# Updated",
                properties = new { },
                tags = Array.Empty<string>(),
                sourceReferences = Array.Empty<object>()
            },
            JsonOptions);
        var snapshot = JsonSerializer.SerializeToElement(
            new
            {
                knowledgeId,
                title = "Original",
                summary = "",
                knowledgeTypeId =
                    Guid.Parse("00000000-0000-4000-8000-000000001001"),
                properties = new { },
                tags = Array.Empty<string>(),
                revision = 1
            },
            JsonOptions);
        var record = CreateRecord(
            DraftTargetObjectType.Knowledge,
            DraftOperationType.Update,
            payload,
            snapshot);

        var (changes, _) = DraftFieldDiffBuilder.Build(record);

        Assert.Contains(
            changes,
            change =>
                change.FieldPath == "contentMarkdown"
                && change.Kind == DraftFieldChangeKind.BaselineUnavailable);
        Assert.Contains(
            changes,
            change =>
                change.FieldPath == "templateId"
                && change.Kind == DraftFieldChangeKind.BaselineUnavailable);
    }

    [Fact]
    public async Task ValidationReturnsFieldPathsWithoutThrowing()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var model = await fixture.QueryAsync(
            context => KnowledgeModelStore.GetAsync(
                context,
                CancellationToken.None));
        var sourcePayload = JsonSerializer.SerializeToElement(
            new
            {
                sourceKind = "web_page",
                title = "Source",
                metadata = new { },
                contentFormat = "markdown",
                contentText = "Body"
            },
            JsonOptions);
        var sourceRecord = CreateRecord(
            DraftTargetObjectType.Source,
            DraftOperationType.Create,
            sourcePayload,
            null);

        var issues = DraftValidationService.Validate(sourceRecord, model);

        var issue = Assert.Single(issues);
        Assert.Equal("metadata.url", issue.FieldPath);
        Assert.Equal(DraftValidationSeverity.Error, issue.Severity);
    }

    [Fact]
    public void CreateDiffMarksEditableFieldsAsNew()
    {
        var record = CreateRecord(
            DraftTargetObjectType.Knowledge,
            DraftOperationType.Create,
            JsonSerializer.SerializeToElement(
                new
                {
                    title = "Title",
                    summary = "",
                    knowledgeTypeId = Guid.NewGuid(),
                    templateId = (string?)null,
                    contentMarkdown = "# Body",
                    properties = new { status = "raw" },
                    tags = new[] { "tag" },
                    sourceReferences = Array.Empty<object>()
                },
                JsonOptions),
            null);

        var (changes, _) = DraftFieldDiffBuilder.Build(record);

        Assert.All(
            changes,
            change => Assert.Equal(DraftFieldChangeKind.New, change.Kind));
        Assert.Contains(changes, change => change.FieldPath == "title");
        Assert.Contains(
            changes,
            change => change.FieldPath == "properties.status");
        Assert.DoesNotContain(
            changes,
            change => change.FieldPath == "sourceReferences");
    }

    [Fact]
    public void UpdateRejectsChangedKnowledgeSourceReferenceOrder()
    {
        var first = SourceReference(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "First");
        var second = SourceReference(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Second");
        var snapshot = KnowledgeUpdateSnapshot([first, second]);
        var payload = KnowledgeUpdatePayload([second, first]);
        var record = CreateRecord(
            DraftTargetObjectType.Knowledge,
            DraftOperationType.Update,
            payload,
            snapshot);

        var exception = Assert.Throws<VaultException>(() =>
            UnifiedDraftService.EnsureImmutableEvidence(record, payload));

        Assert.Equal(
            VaultErrorCode.ImmutableDefinitionConflict,
            exception.Code);
        Assert.Equal(
            "sourceReferences",
            exception.Details["fieldPath"]);
    }

    [Fact]
    public void UpdateRejectsChangedRelationEvidenceButIgnoresObjectOrder()
    {
        var evidence = JsonSerializer.SerializeToElement(
            new
            {
                source = "A",
                locator = new { section = "Intro", paragraph = 1 }
            },
            JsonOptions);
        var sameEvidenceDifferentOrder = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["paragraph"] = 1,
                    ["section"] = "Intro"
                },
                ["source"] = "A"
            },
            JsonOptions);
        var snapshot = RelationUpdateSnapshot(evidence);
        var sameRecord = CreateRecord(
            DraftTargetObjectType.Relation,
            DraftOperationType.Update,
            RelationUpdatePayload(sameEvidenceDifferentOrder),
            snapshot);

        UnifiedDraftService.EnsureImmutableEvidence(
            sameRecord,
            RelationUpdatePayload(sameEvidenceDifferentOrder));

        var changedPayload = RelationUpdatePayload(
            JsonSerializer.SerializeToElement(
                new
                {
                    source = "A",
                    locator = new { section = "Intro", paragraph = 2 }
                },
                JsonOptions));
        var changedRecord = CreateRecord(
            DraftTargetObjectType.Relation,
            DraftOperationType.Update,
            changedPayload,
            snapshot);
        var exception = Assert.Throws<VaultException>(() =>
            UnifiedDraftService.EnsureImmutableEvidence(
                changedRecord,
                changedPayload));

        Assert.Equal(
            VaultErrorCode.ImmutableDefinitionConflict,
            exception.Code);
        Assert.Equal("evidence", exception.Details["fieldPath"]);
    }

    private static CreateDraftInput SourceDraft(string title) =>
        new(
            DraftTargetObjectType.Source,
            DraftOperationType.Create,
            null,
            null,
            SourcePayload(title));

    private static JsonElement SourcePayload(string title) =>
        JsonSerializer.SerializeToElement(
            new
            {
                sourceKind = "web_page",
                title,
                metadata = new { url = "https://example.com" },
                contentFormat = "markdown",
                contentText = "# " + title
            },
            JsonOptions);

    private static DraftRecord CreateRecord(
        DraftTargetObjectType targetType,
        DraftOperationType operation,
        JsonElement payload,
        JsonElement? baseSnapshot) =>
        new(
            Guid.NewGuid(),
            1,
            targetType,
            operation,
            targetType == DraftTargetObjectType.Knowledge
                ? operation == DraftOperationType.Create ? null : Guid.NewGuid()
                : null,
            operation == DraftOperationType.Create ? null : 1,
            1,
            1,
            payload,
            baseSnapshot,
            DraftStatus.Draft,
            "user_ui",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null,
            null,
            null);

    private static JsonElement SourceReference(Guid sourceId, string excerpt) =>
        JsonSerializer.SerializeToElement(
            new
            {
                sourceId,
                sourceRevision = 1,
                locatorType = "section",
                locatorValue = new[] { "Section" },
                excerpt,
                extractionMetadata = new { method = "test" }
            },
            JsonOptions);

    private static JsonElement KnowledgeUpdateSnapshot(
        IReadOnlyList<JsonElement> references) =>
        JsonSerializer.SerializeToElement(
            new
            {
                knowledgeId = Guid.NewGuid(),
                title = "Knowledge",
                summary = "",
                knowledgeTypeId =
                    Guid.Parse("00000000-0000-4000-8000-000000001001"),
                templateId = (string?)null,
                contentMarkdown = "# Knowledge",
                properties = new { },
                tags = Array.Empty<string>(),
                sourceReferences = references,
                revision = 1
            },
            JsonOptions);

    private static JsonElement KnowledgeUpdatePayload(
        IReadOnlyList<JsonElement> references) =>
        JsonSerializer.SerializeToElement(
            new
            {
                title = "Knowledge",
                summary = "",
                knowledgeTypeId =
                    Guid.Parse("00000000-0000-4000-8000-000000001001"),
                templateId = (string?)null,
                contentMarkdown = "# Knowledge",
                properties = new { },
                tags = Array.Empty<string>(),
                sourceReferences = references
            },
            JsonOptions);

    private static JsonElement RelationUpdateSnapshot(JsonElement evidence) =>
        JsonSerializer.SerializeToElement(
            new
            {
                relationId = Guid.NewGuid(),
                relationTypeId =
                    Guid.Parse("00000000-0000-4000-8000-000000002001"),
                sourceKnowledgeId = Guid.NewGuid(),
                targetKnowledgeId = Guid.NewGuid(),
                sourceKnowledgeRevision = 1,
                targetKnowledgeRevision = 1,
                rationale = "Because",
                evidence,
                revision = 1
            },
            JsonOptions);

    private static JsonElement RelationUpdatePayload(JsonElement evidence)
    {
        var snapshot = RelationUpdateSnapshot(evidence);
        return JsonSerializer.SerializeToElement(
            new
            {
                relationTypeId =
                    snapshot.GetProperty("relationTypeId").GetGuid(),
                sourceKnowledgeId =
                    snapshot.GetProperty("sourceKnowledgeId").GetGuid(),
                targetKnowledgeId =
                    snapshot.GetProperty("targetKnowledgeId").GetGuid(),
                sourceKnowledgeRevision = 1,
                targetKnowledgeRevision = 1,
                rationale = "Because",
                evidence
            },
            JsonOptions);
    }

    private static Task<int> CountAuditAsync(
        VaultApplicationFixture fixture,
        string action,
        Guid draftId) =>
        fixture.QueryAsync(async context =>
        {
            using var command = context.CreateCommand();
            command.Command.CommandText =
                """
                SELECT count(*) FROM audit_events
                WHERE action = $action
                  AND target_object_type = 'draft'
                  AND target_id = $draftId;
                """;
            command.Command.Parameters.AddWithValue("$action", action);
            command.Command.Parameters.AddWithValue(
                "$draftId",
                draftId.ToString("D"));
            return Convert.ToInt32(
                await command.Command.ExecuteScalarAsync(
                    CancellationToken.None));
        });
}
