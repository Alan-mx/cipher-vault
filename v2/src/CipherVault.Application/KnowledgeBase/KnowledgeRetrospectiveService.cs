using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

public sealed class KnowledgeRetrospectiveService(
    UnifiedDraftService drafts)
{
    private const int MaxTopicScalars = 193;
    private const int MaxKnowledgeCount = 20;

    public Task<DraftMutation> CreateAsync(
        KnowledgeRetrospectiveCreateInput input,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Only the Web user can create a retrospective draft.");
        }

        var normalized = NormalizeAndValidate(input);
        var requestHash = UnifiedDraftService.Hash(
            "retrospective.create",
            normalized);
        return drafts.CreateAsync(
            requestHash,
            async (context, model, token) =>
            {
                var selected = new List<KnowledgeDetail>(
                    normalized.KnowledgeIds.Count);
                foreach (var knowledgeId in normalized.KnowledgeIds)
                {
                    var item = await KnowledgeStore.GetAsync(
                        context,
                        knowledgeId,
                        token);
                    if (item.Status != KnowledgeStatus.Active)
                    {
                        throw new VaultException(
                            VaultErrorCode.InvalidInput,
                            "Retrospective Knowledge must be active.");
                    }
                    selected.Add(item);
                }
                return KnowledgeRetrospectiveDraftBuilder.Build(
                    normalized,
                    model,
                    selected);
            },
            actor,
            idempotencyKey,
            cancellationToken);
    }

    private static KnowledgeRetrospectiveCreateInput NormalizeAndValidate(
        KnowledgeRetrospectiveCreateInput input)
    {
        var topic = input.Topic
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        var topicLength = topic.EnumerateRunes().Count();
        if (topicLength is < 1 or > MaxTopicScalars)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Topic must contain 1 to {MaxTopicScalars} characters.");
        }
        if (input.KnowledgeIds.Count is < 1 or > MaxKnowledgeCount)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Select 1 to {MaxKnowledgeCount} Knowledge records.");
        }

        var seen = new HashSet<Guid>();
        foreach (var knowledgeId in input.KnowledgeIds)
        {
            if (knowledgeId == Guid.Empty || !seen.Add(knowledgeId))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Knowledge identifiers must be non-empty and unique.");
            }
        }
        return new KnowledgeRetrospectiveCreateInput(
            input.KnowledgeIds.ToArray(),
            topic);
    }
}
