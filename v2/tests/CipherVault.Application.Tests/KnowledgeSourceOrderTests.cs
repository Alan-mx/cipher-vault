using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.Tests;

public sealed class KnowledgeSourceOrderTests
{
    private static readonly Guid NoteTypeId =
        Guid.Parse("00000000-0000-4000-8000-000000001001");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ApprovedKnowledgePreservesSourceReferenceOrder()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var firstSource = await CreateSourceAsync(drafts, user, "First");
        var secondSource = await CreateSourceAsync(drafts, user, "Second");
        var references = new[]
        {
            Reference(secondSource.ObjectId),
            Reference(firstSource.ObjectId)
        };
        var knowledge = await CreateKnowledgeAsync(
            drafts,
            user,
            "Ordered knowledge",
            references);

        var detail = await fixture.QueryAsync(
            context => KnowledgeStore.GetAsync(
                context,
                knowledge.ObjectId,
                CancellationToken.None));

        Assert.Equal(
            [secondSource.ObjectId, firstSource.ObjectId],
            detail.SourceReferences
                .Select(item => item.SourceId)
                .ToArray());
    }

    [Fact]
    public async Task KnowledgeRevisionReadUsesSelectedRevisionMetadata()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var source = await CreateSourceAsync(drafts, user, "Revision source");
        var reference = Reference(source.ObjectId);
        var first = await CreateKnowledgeAsync(
            drafts,
            user,
            "Version one",
            [reference]);
        var second = await ApplyAsync(
            drafts,
            user,
            new CreateDraftInput(
                DraftTargetObjectType.Knowledge,
                DraftOperationType.Update,
                first.ObjectId,
                first.ObjectRevision,
                KnowledgePayload("Version two", [reference])));

        var revision = await fixture.QueryAsync(
            context => KnowledgeStore.GetRevisionAsync(
                context,
                first.ObjectId,
                1,
                CancellationToken.None));

        Assert.Equal("Version one", revision.Title);
        Assert.Equal(1, revision.Revision.Revision);
        Assert.Single(revision.SourceReferences);
        Assert.Equal(2, second.ObjectRevision);
    }

    [Fact]
    public async Task UpdateDraftCannotChangeApprovedSourceReferences()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var drafts = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var source = await CreateSourceAsync(drafts, user, "Immutable source");
        var reference = Reference(source.ObjectId);
        var knowledge = await CreateKnowledgeAsync(
            drafts,
            user,
            "Immutable evidence",
            [reference]);
        var changed = JsonSerializer.SerializeToElement(
            new
            {
                sourceId = source.ObjectId,
                sourceRevision = 1,
                locatorType = "section",
                locatorValue = new[] { "Section" },
                excerpt = "Changed excerpt.",
                extractionMetadata = new { method = "test" }
            },
            JsonOptions);

        var exception = await Assert.ThrowsAsync<CipherVault.Core.Errors.VaultException>(() =>
            drafts.CreateAsync(
                new CreateDraftInput(
                    DraftTargetObjectType.Knowledge,
                    DraftOperationType.Update,
                    knowledge.ObjectId,
                    knowledge.ObjectRevision,
                    KnowledgePayload("Immutable evidence", [changed])),
                user,
                Guid.NewGuid()));

        Assert.Equal(
            CipherVault.Core.Errors.VaultErrorCode.ImmutableDefinitionConflict,
            exception.Code);
    }

    private static async Task<DraftApplyResult> CreateSourceAsync(
        UnifiedDraftService drafts,
        VaultActorContext actor,
        string title)
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                sourceKind = "web_page",
                title,
                metadata = new { url = $"https://example.com/{title}" },
                contentFormat = "markdown",
                contentText = "# Section\n\nClaim text."
            },
            JsonOptions);
        return await ApplyAsync(
            drafts,
            actor,
            new CreateDraftInput(
                DraftTargetObjectType.Source,
                DraftOperationType.Create,
                null,
                null,
                payload));
    }

    private static async Task<DraftApplyResult> CreateKnowledgeAsync(
        UnifiedDraftService drafts,
        VaultActorContext actor,
        string title,
        IReadOnlyList<JsonElement> references)
    {
        return await ApplyAsync(
            drafts,
            actor,
            new CreateDraftInput(
                DraftTargetObjectType.Knowledge,
                DraftOperationType.Create,
                null,
                null,
                KnowledgePayload(title, references)));
    }

    private static JsonElement KnowledgePayload(
        string title,
        IReadOnlyList<JsonElement> references) =>
        JsonSerializer.SerializeToElement(
            new
            {
                title,
                summary = "",
                knowledgeTypeId = NoteTypeId,
                templateId = (string?)null,
                contentMarkdown = "# Ordered knowledge",
                properties = new { },
                tags = Array.Empty<string>(),
                sourceReferences = references
            },
            JsonOptions);

    private static JsonElement Reference(Guid sourceId) =>
        JsonSerializer.SerializeToElement(
            new
            {
                sourceId,
                sourceRevision = 1,
                locatorType = "section",
                locatorValue = new[] { "Section" },
                excerpt = "Claim text.",
                extractionMetadata = new { method = "test" }
            },
            JsonOptions);

    private static async Task<DraftApplyResult> ApplyAsync(
        UnifiedDraftService drafts,
        VaultActorContext actor,
        CreateDraftInput input)
    {
        var created = await drafts.CreateAsync(input, actor, Guid.NewGuid());
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
