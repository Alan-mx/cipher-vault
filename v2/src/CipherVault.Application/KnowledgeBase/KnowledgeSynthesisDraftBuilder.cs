using System.Text;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

internal static class KnowledgeSynthesisDraftBuilder
{
    internal const string DefaultQuestion =
        "围绕所选知识比较共同观点、分歧与冲突、证据质量和适用条件，并形成可审核的综合结论。";

    internal static CreateDraftInput Build(
        KnowledgeSynthesisCreateInput input,
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
        var type = model.KnowledgeTypes.FirstOrDefault(item =>
            item.TypeId == input.KnowledgeTypeId && item.IsEnabled)
            ?? throw new VaultException(
                VaultErrorCode.KnowledgeModelInvalid,
                "The selected synthesis Knowledge type is unavailable.");
        if (input.Topic.EnumerateRunes().Count()
            > KnowledgeWorkflowDraftSupport.MaxTopicScalars)
        {
            throw InvalidInput("Knowledge synthesis topic is too long.");
        }

        var title = $"{input.Topic} · 知识综合";
        var question = string.IsNullOrWhiteSpace(input.Question)
            ? DefaultQuestion
            : input.Question;
        var content = BuildContent(title, question, selectedKnowledge);
        return KnowledgeWorkflowDraftSupport.CreateDraftInput(
            title,
            $"由 {selectedKnowledge.Count} 条知识生成的知识综合脚手架草稿。",
            type,
            content,
            KnowledgeWorkflowDraftSupport.BuildRequiredPropertyDefaults(type),
            KnowledgeWorkflowDraftSupport.BuildSourceReferences(
                selectedKnowledge));
    }

    private static string BuildContent(
        string title,
        string question,
        IReadOnlyList<KnowledgeDetail> selectedKnowledge)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(title).Append("\n\n");
        AppendSection(builder, "一、综合问题与范围", question);
        AppendSection(builder, "二、共同观点");
        AppendSection(builder, "三、分歧与冲突");
        AppendSection(builder, "四、证据质量与适用条件");
        AppendSection(builder, "五、综合结论");
        AppendSection(builder, "六、待验证问题");
        builder.Append("## 七、关联知识\n\n");
        KnowledgeWorkflowDraftSupport.AppendRelatedKnowledge(
            builder,
            selectedKnowledge);
        return builder.ToString();
    }

    private static void AppendSection(
        StringBuilder builder,
        string heading,
        string? body = null)
    {
        builder.Append("## ").Append(heading).Append("\n\n");
        if (body is not null)
        {
            builder.Append(body).Append("\n\n");
        }
    }

    private static VaultException InvalidInput(string message) =>
        new(VaultErrorCode.InvalidInput, message);
}
