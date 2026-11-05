using System.Text;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

internal static class KnowledgeRetrospectiveDraftBuilder
{
    internal static CreateDraftInput Build(
        KnowledgeRetrospectiveCreateInput input,
        KnowledgeModelSnapshot model,
        IReadOnlyList<KnowledgeDetail> selectedKnowledge)
    {
        if (selectedKnowledge.Count != input.KnowledgeIds.Count)
        {
            throw InvalidInput("Selected Knowledge is incomplete.");
        }
        for (var index = 0; index < selectedKnowledge.Count; index++)
        {
            var item = selectedKnowledge[index];
            if (item.KnowledgeId != input.KnowledgeIds[index]
                || item.Status != KnowledgeStatus.Active)
            {
                throw InvalidInput(
                    "Knowledge selection must contain only active Knowledge.");
            }
        }

        var reflectionType = model.KnowledgeTypes.FirstOrDefault(type =>
            type.Key == "reflection" && type.IsEnabled)
            ?? throw new VaultException(
                VaultErrorCode.KnowledgeModelInvalid,
                "An enabled reflection knowledge type is required.");
        var properties =
            KnowledgeWorkflowDraftSupport.BuildRequiredPropertyDefaults(
                reflectionType);
        var title = $"{input.Topic} · 知识复盘";
        if (input.Topic.EnumerateRunes().Count()
            > KnowledgeWorkflowDraftSupport.MaxTopicScalars)
        {
            throw InvalidInput("Knowledge retrospective topic is too long.");
        }

        var content = BuildContent(title, selectedKnowledge);
        return KnowledgeWorkflowDraftSupport.CreateDraftInput(
            title,
            $"由 {selectedKnowledge.Count} 条知识生成的知识复盘草稿。",
            reflectionType,
            content,
            properties,
            KnowledgeWorkflowDraftSupport.BuildSourceReferences(
                selectedKnowledge));
    }

    private static string BuildContent(
        string title,
        IReadOnlyList<KnowledgeDetail> selectedKnowledge)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(title).Append("\n\n");
        AppendSection(builder, "目标与背景");
        AppendSection(builder, "过程与关键决策");
        AppendSection(builder, "结果与验证");
        AppendSection(builder, "问题与原因");
        AppendSection(builder, "经验与可复用知识");
        AppendSection(builder, "后续行动");
        builder.Append("## 关联知识\n\n");
        KnowledgeWorkflowDraftSupport.AppendRelatedKnowledge(
            builder,
            selectedKnowledge);
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string heading) =>
        builder.Append("## ").Append(heading).Append("\n\n");

    private static VaultException InvalidInput(string message) =>
        new(VaultErrorCode.InvalidInput, message);
}
