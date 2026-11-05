using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

public sealed class KnowledgeSynthesisService(
    UnifiedDraftService drafts)
{
    public Task<DraftMutation> CreateAsync(
        KnowledgeSynthesisCreateInput input,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Only the Web user can create a synthesis draft.");
        }
        var normalized = NormalizeAndValidate(input);
        var requestHash = UnifiedDraftService.Hash(
            "synthesis.create",
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
                            "Synthesis Knowledge must be active.");
                    }
                    selected.Add(item);
                }
                return KnowledgeSynthesisDraftBuilder.Build(
                    normalized,
                    model,
                    selected);
            },
            actor,
            idempotencyKey,
            cancellationToken);
    }

    private static KnowledgeSynthesisCreateInput NormalizeAndValidate(
        KnowledgeSynthesisCreateInput input)
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
        var question =
            KnowledgeWorkflowDraftSupport.NormalizeQuestion(input.Question);
        if (question is not null
            && question.EnumerateRunes().Count()
                > KnowledgeWorkflowDraftSupport.MaxQuestionScalars)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Synthesis question must not exceed {KnowledgeWorkflowDraftSupport.MaxQuestionScalars} characters.");
        }
        if (input.KnowledgeTypeId == Guid.Empty)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Knowledge type is required.");
        }
        KnowledgeWorkflowDraftSupport.ValidateKnowledgeIds(input.KnowledgeIds);
        return new KnowledgeSynthesisCreateInput(
            input.KnowledgeIds.ToArray(),
            topic,
            question,
            input.KnowledgeTypeId);
    }
}
