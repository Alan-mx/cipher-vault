using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

internal static class KnowledgeRetrospectiveDraftBuilder
{
    private const int MaxTopicScalars = 193;
    private const int MaxKnowledgeTitleScalars = 80;
    private const int MaxSourceReferences = 8;
    private const int MaxContentScalars = 64_000;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

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
        var properties = BuildDefaultProperties(reflectionType);
        var title = $"{input.Topic} · 知识复盘";
        if (title.Length is < 1 or > 200)
        {
            throw InvalidInput("Knowledge retrospective title is too long.");
        }
        if (input.Topic.EnumerateRunes().Count() > MaxTopicScalars)
        {
            throw InvalidInput("Knowledge retrospective topic is too long.");
        }

        var content = BuildContent(title, selectedKnowledge);
        if (content.EnumerateRunes().Count() > MaxContentScalars)
        {
            throw new VaultException(
                VaultErrorCode.PayloadTooLarge,
                "Knowledge retrospective content is too large.");
        }

        var payload = JsonSerializer.SerializeToElement(
            new KnowledgeMutationPayload(
                title,
                $"由 {selectedKnowledge.Count} 条知识生成的知识复盘草稿。",
                reflectionType.TypeId,
                null,
                content,
                properties,
                [],
                BuildSourceReferences(selectedKnowledge)),
            JsonOptions);
        return new CreateDraftInput(
            DraftTargetObjectType.Knowledge,
            DraftOperationType.Create,
            null,
            null,
            payload);
    }

    private static JsonElement BuildDefaultProperties(
        KnowledgeTypeDefinition reflectionType)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in reflectionType.Properties.Where(item =>
                     item.IsEnabled && item.Required))
        {
            if (property.DefaultValue is not { } defaultValue)
            {
                throw new VaultException(
                    VaultErrorCode.KnowledgeModelInvalid,
                    $"Required reflection property {property.Key} has no default value.");
            }
            values.Add(property.Key, defaultValue.Clone());
        }
        return JsonSerializer.SerializeToElement(values, JsonOptions);
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
        for (var index = 0; index < selectedKnowledge.Count; index++)
        {
            var item = selectedKnowledge[index];
            builder.Append(index + 1)
                .Append(". [")
                .Append(FormatLinkText(item.Title))
                .Append("](/knowledge/")
                .Append(item.KnowledgeId.ToString("D"))
                .Append(")\n");
        }
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string heading) =>
        builder.Append("## ").Append(heading).Append("\n\n");

    private static string FormatLinkText(string title)
    {
        var normalized = title
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        var runes = normalized.EnumerateRunes().ToArray();
        var truncated = runes.Length > MaxKnowledgeTitleScalars
            ? string.Concat(
                runes.Take(MaxKnowledgeTitleScalars)
                    .Select(rune => rune.ToString())) + "…"
            : normalized;
        return truncated
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }

    private static IReadOnlyList<SourceReferenceInput> BuildSourceReferences(
        IReadOnlyList<KnowledgeDetail> selectedKnowledge)
    {
        var references = new List<SourceReferenceInput>(MaxSourceReferences);
        var seen = new HashSet<(Guid SourceId, int Revision)>();
        foreach (var knowledge in selectedKnowledge)
        {
            foreach (var reference in knowledge.SourceReferences)
            {
                if (!seen.Add((reference.SourceId, reference.SourceRevision)))
                {
                    continue;
                }
                references.Add(new SourceReferenceInput(
                    reference.SourceId,
                    reference.SourceRevision,
                    reference.LocatorType,
                    reference.LocatorValue.Clone(),
                    reference.Excerpt,
                    reference.ExtractionMetadata.Clone()));
                if (references.Count == MaxSourceReferences)
                {
                    return references;
                }
            }
        }
        return references;
    }

    private static VaultException InvalidInput(string message) =>
        new(VaultErrorCode.InvalidInput, message);
}
