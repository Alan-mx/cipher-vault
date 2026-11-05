using CipherVault.Core.Documents;
using CipherVault.Core.Knowledge;
using CipherVault.Core.Storage.Knowledge;

namespace CipherVault.Application.Tests;

public sealed class KnowledgeMaturityQueueStoreTests
{
    [Fact]
    public async Task QueueClassifiesAllMaturityShapesAndExcludesDeleted()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        _ = await CreateAsync(fixture, actor, "Raw", "maturity: RAW");
        _ = await CreateAsync(fixture, actor, "Excerpt", "maturity: excerpt");
        _ = await CreateAsync(
            fixture,
            actor,
            "Organized",
            "maturity: \" Organized \"\ntags: [Work]");
        _ = await CreateAsync(fixture, actor, "Verified", "maturity: verified");
        _ = await CreateAsync(fixture, actor, "Practiced", "maturity: practiced");
        _ = await CreateAsync(fixture, actor, "Missing", "status: active");
        _ = await CreateAsync(fixture, actor, "Null", "maturity: null");
        _ = await CreateAsync(fixture, actor, "Number", "maturity: 7");
        _ = await CreateAsync(fixture, actor, "Boolean", "maturity: true");
        _ = await CreateAsync(fixture, actor, "Array", "maturity: [A, B]");
        _ = await CreateAsync(
            fixture,
            actor,
            "Object",
            "maturity:\n  key: value");
        _ = await CreateAsync(fixture, actor, "Blank", "maturity: \"   \"");
        _ = await CreateAsync(fixture, actor, "Other", "maturity: reviewed");
        _ = await CreateAsync(
            fixture,
            actor,
            "Wrong Key Case",
            "Maturity: organized");
        _ = await CreateTextAsync(fixture, actor, "Text Document");
        var deleted = await CreateAsync(
            fixture,
            actor,
            "Deleted Organized",
            "maturity: organized");
        await fixture.Documents.DeleteAsync(deleted, 1, actor);

        var pending = await ListAsync(
            fixture,
            KnowledgeMaturityQueueBucket.PendingOrganization);
        Assert.Equal(11, pending.Items.Count);
        Assert.Contains(pending.Items, item => item.Title == "Raw");
        Assert.Contains(pending.Items, item => item.Title == "Text Document");
        Assert.Contains(
            pending.Items,
            item => item.Title == "Wrong Key Case"
                && item.MaturityValueType ==
                KnowledgeMaturityValueType.Missing);
        AssertStandard(
            Assert.Single(pending.Items, item => item.Title == "Missing"),
            KnowledgeMaturityValueType.Missing,
            null,
            KnowledgeMaturityLevel.Raw);
        AssertStandard(
            Assert.Single(pending.Items, item => item.Title == "Null"),
            KnowledgeMaturityValueType.Null,
            null,
            KnowledgeMaturityLevel.Raw);
        AssertStandard(
            Assert.Single(pending.Items, item => item.Title == "Number"),
            KnowledgeMaturityValueType.Number,
            null,
            KnowledgeMaturityLevel.Raw);
        AssertStandard(
            Assert.Single(pending.Items, item => item.Title == "Boolean"),
            KnowledgeMaturityValueType.Boolean,
            null,
            KnowledgeMaturityLevel.Raw);
        AssertStandard(
            Assert.Single(pending.Items, item => item.Title == "Array"),
            KnowledgeMaturityValueType.Array,
            null,
            KnowledgeMaturityLevel.Raw);
        AssertStandard(
            Assert.Single(pending.Items, item => item.Title == "Object"),
            KnowledgeMaturityValueType.Object,
            null,
            KnowledgeMaturityLevel.Raw);
        AssertStandard(
            Assert.Single(pending.Items, item => item.Title == "Blank"),
            KnowledgeMaturityValueType.String,
            "   ",
            KnowledgeMaturityLevel.Raw);

        var verification = await ListAsync(
            fixture,
            KnowledgeMaturityQueueBucket.PendingVerification);
        var organized = Assert.Single(verification.Items);
        Assert.Equal("Organized", organized.Title);
        Assert.Equal(" Organized ", organized.MaturityValue);
        Assert.Equal(
            KnowledgeMaturityValueType.String,
            organized.MaturityValueType);
        Assert.Equal(
            KnowledgeMaturityLevel.Verified,
            organized.NextMaturity);
        Assert.Equal(["Work"], organized.Tags);

        var completed = await ListAsync(
            fixture,
            KnowledgeMaturityQueueBucket.Completed);
        Assert.Equal(2, completed.Items.Count);
        Assert.Contains(
            completed.Items,
            item => item.Title == "Verified"
                && item.NextMaturity == KnowledgeMaturityLevel.Practiced);
        Assert.Contains(
            completed.Items,
            item => item.Title == "Practiced"
                && item.NextMaturity is null);

        var other = await ListAsync(
            fixture,
            KnowledgeMaturityQueueBucket.Other);
        var reviewed = Assert.Single(other.Items);
        Assert.Equal("reviewed", reviewed.MaturityValue);
        Assert.Null(reviewed.NextMaturity);
    }

    [Fact]
    public async Task QueueUsesStableKeysetCursorAndRejectsScopeChanges()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        for (var index = 1; index <= 4; index++)
        {
            _ = await CreateAsync(
                fixture,
                actor,
                $"Organized {index}",
                "maturity: organized");
        }

        var first = await ListAsync(
            fixture,
            KnowledgeMaturityQueueBucket.PendingVerification,
            pageSize: 2);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = await ListAsync(
            fixture,
            KnowledgeMaturityQueueBucket.PendingVerification,
            pageSize: 2,
            cursor: first.NextCursor);
        Assert.Equal(2, second.Items.Count);
        Assert.Null(second.NextCursor);
        Assert.Equal(
            4,
            first.Items
                .Concat(second.Items)
                .Select(item => item.DocumentId)
                .Distinct()
                .Count());

        await Assert.ThrowsAsync<CipherVault.Core.Errors.VaultException>(() =>
            ListAsync(
                fixture,
                KnowledgeMaturityQueueBucket.Completed,
                pageSize: 2,
                cursor: first.NextCursor));
        await Assert.ThrowsAsync<CipherVault.Core.Errors.VaultException>(() =>
            ListAsync(
                fixture,
                KnowledgeMaturityQueueBucket.PendingVerification,
                pageSize: 3,
                cursor: first.NextCursor));
    }

    private static Task<KnowledgeMaturityQueuePage> ListAsync(
        VaultApplicationFixture fixture,
        KnowledgeMaturityQueueBucket bucket,
        int? pageSize = 30,
        string? cursor = null) =>
        fixture.QueryAsync(context =>
            KnowledgeMaturityQueueStore.ListAsync(
                context,
                new KnowledgeMaturityQueueQuery(
                    bucket,
                    new CursorPageRequest(pageSize, cursor)),
                CancellationToken.None));

    private static async Task<Guid> CreateAsync(
        VaultApplicationFixture fixture,
        VaultActorContext actor,
        string title,
        string frontMatter)
    {
        var result = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                title,
                null,
                null,
                VaultDocumentFormat.Markdown,
                $"""
                 ---
                 {frontMatter}
                 ---

                 # {title}
                 """,
                null),
            actor);
        return result.DocumentId;
    }

    private static async Task<Guid> CreateTextAsync(
        VaultApplicationFixture fixture,
        VaultActorContext actor,
        string title)
    {
        var result = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                title,
                null,
                null,
                VaultDocumentFormat.Text,
                "Plain text.",
                null),
            actor);
        return result.DocumentId;
    }

    private static void AssertStandard(
        KnowledgeMaturityQueueItem item,
        KnowledgeMaturityValueType valueType,
        string? value,
        KnowledgeMaturityLevel next)
    {
        Assert.Equal(valueType, item.MaturityValueType);
        Assert.Equal(value, item.MaturityValue);
        Assert.Equal(next, item.NextMaturity);
    }
}
