using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.Tests;

public sealed class KnowledgeSynthesisServiceTests
{
    private static readonly Guid ArticleTypeId =
        Guid.Parse("00000000-0000-4000-8000-000000001002");
    private static readonly Guid NoteTypeId =
        Guid.Parse("00000000-0000-4000-8000-000000001001");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void BuilderCreatesFixedScaffoldAndPreservesOrder()
    {
        var (model, articleType) = ReadModel();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = CreateKnowledge(firstId, articleType, "First");
        var second = CreateKnowledge(secondId, articleType, "Second");

        var result = KnowledgeSynthesisDraftBuilder.Build(
            new KnowledgeSynthesisCreateInput(
                [secondId, firstId],
                "Architecture review",
                null,
                ArticleTypeId),
            model,
            [second, first]);

        var payload = result.Payload;
        Assert.Equal(
            "Architecture review · 知识综合",
            payload.GetProperty("title").GetString());
        Assert.Equal(
            "由 2 条知识生成的知识综合脚手架草稿。",
            payload.GetProperty("summary").GetString());
        Assert.Equal(
            ArticleTypeId,
            payload.GetProperty("knowledgeTypeId").GetGuid());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("templateId").ValueKind);
        var content = payload.GetProperty("contentMarkdown").GetString()!;
        Assert.Contains(
            "## 一、综合问题与范围",
            content,
            StringComparison.Ordinal);
        Assert.Contains(KnowledgeSynthesisDraftBuilder.DefaultQuestion, content);
        Assert.Contains("## 二、共同观点", content, StringComparison.Ordinal);
        Assert.Contains("## 三、分歧与冲突", content, StringComparison.Ordinal);
        Assert.Contains(
            "## 四、证据质量与适用条件",
            content,
            StringComparison.Ordinal);
        Assert.Contains("## 五、综合结论", content, StringComparison.Ordinal);
        Assert.Contains("## 六、待验证问题", content, StringComparison.Ordinal);
        Assert.Contains("## 七、关联知识", content, StringComparison.Ordinal);
        Assert.True(
            content.IndexOf(secondId.ToString("D"), StringComparison.Ordinal)
            < content.IndexOf(firstId.ToString("D"), StringComparison.Ordinal));
    }

    [Fact]
    public void BuilderUsesQuestionSourcesAndRequiredDefaults()
    {
        var (model, articleType) = ReadModel(withRequiredDefault: true);
        var knowledgeId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var reference = new SourceReferenceView(
            Guid.NewGuid(),
            sourceId,
            1,
            SourceLocatorType.Section,
            JsonSerializer.SerializeToElement(new[] { "Section" }),
            "Evidence",
            JsonSerializer.SerializeToElement(new { method = "test" }));
        var knowledge = CreateKnowledge(
            knowledgeId,
            articleType,
            "Knowledge",
            [reference, reference]);

        var result = KnowledgeSynthesisDraftBuilder.Build(
            new KnowledgeSynthesisCreateInput(
                [knowledgeId],
                "Review",
                "What agrees and what conflicts?",
                ArticleTypeId),
            model,
            [knowledge]);

        Assert.Contains(
            "What agrees and what conflicts?",
            result.Payload.GetProperty("contentMarkdown").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            "default",
            result.Payload.GetProperty("properties")
                .GetProperty("status")
                .GetString());
        Assert.Equal(
            1,
            result.Payload.GetProperty("sourceReferences").GetArrayLength());
        Assert.Equal(
            sourceId,
            result.Payload.GetProperty("sourceReferences")[0]
                .GetProperty("sourceId")
                .GetGuid());
    }

    [Fact]
    public void BuilderRejectsDisabledTypeAndMissingDefault()
    {
        var (model, articleType) = ReadModel();
        var id = Guid.NewGuid();
        var knowledge = CreateKnowledge(id, articleType, "Knowledge");
        var disabled = Assert.Throws<VaultException>(() =>
            KnowledgeSynthesisDraftBuilder.Build(
                new KnowledgeSynthesisCreateInput(
                    [id],
                    "Review",
                    null,
                    ArticleTypeId),
                model with
                {
                    KnowledgeTypes =
                    [
                        articleType with { IsEnabled = false }
                    ]
                },
                [knowledge]));
        Assert.Equal(VaultErrorCode.KnowledgeModelInvalid, disabled.Code);

        var required = articleType with
        {
            Properties =
            [
                new KnowledgePropertyDefinition(
                    Guid.NewGuid(),
                    "status",
                    "Status",
                    "string",
                    true,
                    null,
                    JsonSerializer.SerializeToElement(new { }),
                    true,
                    0)
            ]
        };
        var missingDefault = Assert.Throws<VaultException>(() =>
            KnowledgeSynthesisDraftBuilder.Build(
                new KnowledgeSynthesisCreateInput(
                    [id],
                    "Review",
                    null,
                    ArticleTypeId),
                model with { KnowledgeTypes = [required] },
                [CreateKnowledge(id, required, "Knowledge")]));
        Assert.Equal(VaultErrorCode.KnowledgeModelInvalid, missingDefault.Code);
    }

    [Fact]
    public async Task CreateIsIdempotentAndValidatesQuestion()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var service = new KnowledgeSynthesisService(drafts);
        var knowledge = await CreateKnowledgeDraftAsync(drafts, actor, "Knowledge");
        var input = new KnowledgeSynthesisCreateInput(
            [knowledge.ObjectId],
            "Synthesis",
            "Compare evidence.",
            ArticleTypeId);
        var key = Guid.NewGuid();

        var first = await service.CreateAsync(input, actor, key);
        var replay = await service.CreateAsync(input, actor, key);

        Assert.Equal(first.DraftId, replay.DraftId);
        var oversized = await Assert.ThrowsAsync<VaultException>(() =>
            service.CreateAsync(
                input with { Question = new string('x', 4_001) },
                actor,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.InvalidInput, oversized.Code);
        var missingType = await Assert.ThrowsAsync<VaultException>(() =>
            service.CreateAsync(
                input with { KnowledgeTypeId = Guid.Empty },
                actor,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.InvalidInput, missingType.Code);
    }

    private static (KnowledgeModelSnapshot Model, KnowledgeTypeDefinition Article)
        ReadModel(bool withRequiredDefault = false)
    {
        var article = new KnowledgeTypeDefinition(
            ArticleTypeId,
            "article",
            "文章",
            null,
            true,
            20,
            withRequiredDefault
                ?
                [
                    new KnowledgePropertyDefinition(
                        Guid.NewGuid(),
                        "status",
                        "Status",
                        "string",
                        true,
                        JsonSerializer.SerializeToElement("default"),
                        JsonSerializer.SerializeToElement(new { }),
                        true,
                        0)
                ]
                : []);
        var note = new KnowledgeTypeDefinition(
            NoteTypeId,
            "note",
            "笔记",
            null,
            true,
            10,
            []);
        return (
            new KnowledgeModelSnapshot(
                1,
                DateTimeOffset.UnixEpoch,
                [note, article],
                [],
                [],
                [],
                []),
            article);
    }

    private static KnowledgeDetail CreateKnowledge(
        Guid id,
        KnowledgeTypeDefinition type,
        string title,
        IReadOnlyList<SourceReferenceView>? references = null) =>
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
            references ?? [],
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static async Task<DraftApplyResult> CreateKnowledgeDraftAsync(
        UnifiedDraftService drafts,
        VaultActorContext actor,
        string title)
    {
        var payload = JsonSerializer.SerializeToElement(
            new KnowledgeMutationPayload(
                title,
                string.Empty,
                ArticleTypeId,
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
}
