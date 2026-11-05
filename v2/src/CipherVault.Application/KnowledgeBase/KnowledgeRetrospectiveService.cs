using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

public sealed class KnowledgeRetrospectiveService(
    UnifiedDraftService drafts)
{
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
        var topic = KnowledgeWorkflowDraftSupport.NormalizeTopic(input.Topic);
        var topicLength = topic.EnumerateRunes().Count();
        if (topicLength is < 1
            or > KnowledgeWorkflowDraftSupport.MaxTopicScalars)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Topic must contain 1 to {KnowledgeWorkflowDraftSupport.MaxTopicScalars} characters.");
        }
        KnowledgeWorkflowDraftSupport.ValidateKnowledgeIds(input.KnowledgeIds);
        return new KnowledgeRetrospectiveCreateInput(
            input.KnowledgeIds.ToArray(),
            topic);
    }
}
