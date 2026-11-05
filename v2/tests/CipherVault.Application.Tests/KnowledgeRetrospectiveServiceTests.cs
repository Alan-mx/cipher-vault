using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.Tests;

public sealed class KnowledgeRetrospectiveServiceTests
{
    private static readonly Guid ReflectionTypeId =
        Guid.Parse("00000000-0000-4000-8000-000000001006");

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void BuilderPreservesKnowledgeAndSourceOrder()
    {
        var (model, reflectionType) = ReadSeedModel();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstSource = Guid.NewGuid();
        var secondSource = Guid.NewGuid();
        var first = CreateKnowledge(
            firstId,
            reflectionType,
            "First",
            [CreateSourceReference(firstSource)]);
        var second = CreateKnowledge(
            secondId,
            reflectionType,
            "Second",
            [CreateSourceReference(secondSource)]);

        var input = new KnowledgeRetrospectiveCreateInput(
            [secondId, firstId],
            "Release");
        var result = KnowledgeRetrospectiveDraftBuilder.Build(
            input,
            model,
            [second, first]);

        var payload = result.Payload;
        Assert.Equal("Release · 知识复盘", payload.GetProperty("title").GetString());
        Assert.Equal(
            ReflectionTypeId,
            payload.GetProperty("knowledgeTypeId").GetGuid());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("templateId").ValueKind);
        var content = payload.GetProperty("contentMarkdown").GetString()!;
        Assert.Contains("## 目标与背景", content, StringComparison.Ordinal);
        Assert.Contains("## 过程与关键决策", content, StringComparison.Ordinal);
        Assert.Contains("## 结果与验证", content, StringComparison.Ordinal);
        Assert.Contains("## 问题与原因", content, StringComparison.Ordinal);
        Assert.Contains("## 经验与可复用知识", content, StringComparison.Ordinal);
        Assert.Contains("## 后续行动", content, StringComparison.Ordinal);
        Assert.True(
            content.IndexOf(secondId.ToString("D"), StringComparison.Ordinal)
            < content.IndexOf(firstId.ToString("D"), StringComparison.Ordinal));
        var references = payload.GetProperty("sourceReferences")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, references.Length);
        Assert.Equal(
            secondSource,
            references[0].GetProperty("sourceId").GetGuid());
        Assert.Equal(
            firstSource,
            references[1].GetProperty("sourceId").GetGuid());
    }

    [Fact]
    public void BuilderEscapesTruncatesAndCapsReferences()
    {
        var (model, reflectionType) = ReadSeedModel();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var references = Enumerable.Range(0, 6)
            .Select(_ => CreateSourceReference(Guid.NewGuid()))
            .Concat(Enumerable.Range(0, 6)
                .Select(_ => CreateSourceReference(Guid.NewGuid())))
            .ToArray();
        var first = CreateKnowledge(
            firstId,
            reflectionType,
            $"[{new string('a', 100)}]",
            references);
        var second = CreateKnowledge(
            secondId,
            reflectionType,
            "Second",
            []);

        var result = KnowledgeRetrospectiveDraftBuilder.Build(
            new KnowledgeRetrospectiveCreateInput([firstId, secondId], "Review"),
            model,
            [first, second]);

        var content = result.Payload.GetProperty("contentMarkdown").GetString()!;
        Assert.Contains("\\[", content, StringComparison.Ordinal);
        Assert.Contains("…", content, StringComparison.Ordinal);
        Assert.Equal(
            8,
            result.Payload.GetProperty("sourceReferences").GetArrayLength());
    }

    [Fact]
    public void BuilderUsesRequiredDefaultsAndRejectsMissingDefault()
    {
        var (model, reflectionType) = ReadSeedModel();
        var propertyId = Guid.NewGuid();
        var withDefault = reflectionType with
        {
            Properties =
            [
                new KnowledgePropertyDefinition(
                    propertyId,
                    "outcome",
                    "Outcome",
                    "string",
                    true,
                    JsonSerializer.SerializeToElement("unknown"),
                    JsonSerializer.SerializeToElement(new { }),
                    true,
                    0)
            ]
        };
        var knowledge = CreateKnowledge(
            Guid.NewGuid(),
            withDefault,
            "Knowledge",
            []);
        var modelWithDefault = ReplaceReflection(model, withDefault);

        var result = KnowledgeRetrospectiveDraftBuilder.Build(
            new KnowledgeRetrospectiveCreateInput(
                [knowledge.KnowledgeId],
                "Default"),
            modelWithDefault,
            [knowledge]);

        Assert.Equal(
            "unknown",
            result.Payload.GetProperty("properties")
                .GetProperty("outcome")
                .GetString());

        var withoutDefault = withDefault with
        {
            Properties =
            [
                withDefault.Properties[0] with { DefaultValue = null }
            ]
        };
        var invalidModel = ReplaceReflection(model, withoutDefault);
        var exception = Assert.Throws<VaultException>(() =>
            KnowledgeRetrospectiveDraftBuilder.Build(
                new KnowledgeRetrospectiveCreateInput(
                    [knowledge.KnowledgeId],
                    "Default"),
                invalidModel,
                [CreateKnowledge(
                    knowledge.KnowledgeId,
                    withoutDefault,
                    "Knowledge",
                    [])]));
        Assert.Equal(VaultErrorCode.KnowledgeModelInvalid, exception.Code);
    }

    [Fact]
    public void BuilderRejectsMissingReflectionAndInactiveKnowledge()
    {
        var (model, reflectionType) = ReadSeedModel();
        var id = Guid.NewGuid();
        var missingType = Assert.Throws<VaultException>(() =>
            KnowledgeRetrospectiveDraftBuilder.Build(
                new KnowledgeRetrospectiveCreateInput([id], "Review"),
                model with { KnowledgeTypes = [] },
                [CreateKnowledge(id, reflectionType, "Knowledge", [])]));
        Assert.Equal(VaultErrorCode.KnowledgeModelInvalid, missingType.Code);

        var archived = CreateKnowledge(
            id,
            reflectionType,
            "Knowledge",
            []) with
        {
            Status = KnowledgeStatus.Archived
        };
        var inactive = Assert.Throws<VaultException>(() =>
            KnowledgeRetrospectiveDraftBuilder.Build(
                new KnowledgeRetrospectiveCreateInput([id], "Review"),
                model,
                [archived]));
        Assert.Equal(VaultErrorCode.InvalidInput, inactive.Code);
    }

    [Fact]
    public async Task CreateIsIdempotentAfterKnowledgeIsArchived()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var service = new KnowledgeRetrospectiveService(drafts);
        var knowledge = await CreateKnowledgeDraftAsync(
            drafts,
            actor,
            "Knowledge");
        var input = new KnowledgeRetrospectiveCreateInput(
            [knowledge.ObjectId],
            "Archive retry");
        var key = Guid.NewGuid();

        var first = await service.CreateAsync(input, actor, key);
        await ArchiveKnowledgeAsync(
            drafts,
            actor,
            knowledge.ObjectId,
            knowledge.ObjectRevision);
        var replay = await service.CreateAsync(input, actor, key);

        Assert.Equal(first.DraftId, replay.DraftId);
        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            service.CreateAsync(input, actor, Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public async Task CreateRejectsDuplicatesAndMcpActor()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var service = new KnowledgeRetrospectiveService(drafts);
        var id = Guid.NewGuid();
        var duplicate = await Assert.ThrowsAsync<VaultException>(() =>
            service.CreateAsync(
                new KnowledgeRetrospectiveCreateInput([id, id], "Duplicate"),
                new VaultActorContext(VaultActorKind.UserUi),
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.InvalidInput, duplicate.Code);

        var forbidden = await Assert.ThrowsAsync<VaultException>(() =>
            service.CreateAsync(
                new KnowledgeRetrospectiveCreateInput([id], "Mcp"),
                new VaultActorContext(VaultActorKind.Mcp),
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.Forbidden, forbidden.Code);
    }

    [Fact]
    public async Task CreateRejectsMissingAndOversizedTopic()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var service = new KnowledgeRetrospectiveService(drafts);
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var id = Guid.NewGuid();

        var missing = await Assert.ThrowsAsync<VaultException>(() =>
            service.CreateAsync(
                new KnowledgeRetrospectiveCreateInput([id], "   "),
                actor,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.InvalidInput, missing.Code);

        var oversized = await Assert.ThrowsAsync<VaultException>(() =>
            service.CreateAsync(
                new KnowledgeRetrospectiveCreateInput(
                    [id],
                    new string('x', 194)),
                actor,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.InvalidInput, oversized.Code);
    }

    private static (KnowledgeModelSnapshot Model, KnowledgeTypeDefinition Type)
        ReadSeedModel()
    {
        var type = new KnowledgeTypeDefinition(
            ReflectionTypeId,
            "reflection",
            "反思",
            null,
            true,
            10,
            []);
        return (
            new KnowledgeModelSnapshot(1, DateTimeOffset.UnixEpoch, [type], [], [], [], []),
            type);
    }

    private static KnowledgeModelSnapshot ReplaceReflection(
        KnowledgeModelSnapshot model,
        KnowledgeTypeDefinition reflectionType) =>
        model with { KnowledgeTypes = [reflectionType] };

    private static KnowledgeDetail CreateKnowledge(
        Guid id,
        KnowledgeTypeDefinition type,
        string title,
        IReadOnlyList<SourceReferenceView> references) =>
        new(
            id,
            title,
            string.Empty,
            type.TypeId,
            type.Key,
            null,
            KnowledgeStatus.Active,
            1,
            Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new { }),
            [],
            references,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static SourceReferenceView CreateSourceReference(Guid sourceId) =>
        new(
            Guid.NewGuid(),
            sourceId,
            1,
            SourceLocatorType.Section,
            JsonSerializer.SerializeToElement(new[] { "Section" }),
            $"Excerpt for {sourceId:D}",
            JsonSerializer.SerializeToElement(new { method = "test" }));

    private static async Task<DraftApplyResult> CreateKnowledgeDraftAsync(
        UnifiedDraftService drafts,
        VaultActorContext actor,
        string title)
    {
        var payload = JsonSerializer.SerializeToElement(
            new KnowledgeMutationPayload(
                title,
                string.Empty,
                ReflectionTypeId,
                null,
                "# Knowledge",
                JsonSerializer.SerializeToElement(new { }),
                [],
                []),
            JsonOptions);
        var created = await drafts.CreateAsync(
            new CreateDraftInput(
                DraftTargetObjectType.Knowledge,
                DraftOperationType.Create,
                null,
                null,
                payload),
            actor,
            Guid.NewGuid());
        var submitted = await drafts.SubmitAsync(
            created.DraftId,
            created.DraftRevision,
            actor,
            Guid.NewGuid());
        return await drafts.ApplyAsync(
            submitted.DraftId,
            submitted.DraftRevision,
            actor,
            Guid.NewGuid());
    }

    private static async Task ArchiveKnowledgeAsync(
        UnifiedDraftService drafts,
        VaultActorContext actor,
        Guid knowledgeId,
        int revision)
    {
        var created = await drafts.CreateAsync(
            new CreateDraftInput(
                DraftTargetObjectType.Knowledge,
                DraftOperationType.Archive,
                knowledgeId,
                revision,
                JsonSerializer.SerializeToElement(new { })),
            actor,
            Guid.NewGuid());
        var submitted = await drafts.SubmitAsync(
            created.DraftId,
            created.DraftRevision,
            actor,
            Guid.NewGuid());
        _ = await drafts.ApplyAsync(
            submitted.DraftId,
            submitted.DraftRevision,
            actor,
            Guid.NewGuid());
    }
}
