using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

internal static class KnowledgeWorkflowDraftSupport
{
    internal const int MaxTopicScalars = 193;
    internal const int MaxKnowledgeCount = 20;
    internal const int MaxQuestionScalars = 4_000;
    internal const int MaxKnowledgeTitleScalars = 80;
    internal const int MaxSourceReferences = 8;
    internal const int MaxContentScalars = 64_000;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal static string NormalizeTopic(string topic) =>
        topic
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    internal static string? NormalizeQuestion(string? question)
    {
        if (question is null)
        {
            return null;
        }
        var normalized = question
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    internal static void ValidateKnowledgeIds(IReadOnlyList<Guid> ids)
    {
        if (ids.Count is < 1 or > MaxKnowledgeCount)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Select 1 to {MaxKnowledgeCount} Knowledge records.");
        }
        var seen = new HashSet<Guid>();
        foreach (var knowledgeId in ids)
        {
            if (knowledgeId == Guid.Empty || !seen.Add(knowledgeId))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Knowledge identifiers must be non-empty and unique.");
            }
        }
    }

    internal static string FormatKnowledgeLinkText(string title)
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

    internal static JsonElement BuildRequiredPropertyDefaults(
        KnowledgeTypeDefinition type)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in type.Properties.Where(item =>
                     item.IsEnabled && item.Required))
        {
            if (property.DefaultValue is not { } defaultValue)
            {
                throw new VaultException(
                    VaultErrorCode.KnowledgeModelInvalid,
                    $"Required Knowledge property {property.Key} has no default value.");
            }
            values.Add(property.Key, defaultValue.Clone());
        }
        return JsonSerializer.SerializeToElement(values, JsonOptions);
    }

    internal static IReadOnlyList<SourceReferenceInput> BuildSourceReferences(
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

    internal static void AppendRelatedKnowledge(
        StringBuilder builder,
        IReadOnlyList<KnowledgeDetail> selectedKnowledge)
    {
        for (var index = 0; index < selectedKnowledge.Count; index++)
        {
            var item = selectedKnowledge[index];
            builder.Append(index + 1)
                .Append(". [")
                .Append(FormatKnowledgeLinkText(item.Title))
                .Append("](/knowledge/")
                .Append(item.KnowledgeId.ToString("D"))
                .Append(")\n");
        }
    }

    internal static void EnsureContentLength(string content)
    {
        if (content.EnumerateRunes().Count() > MaxContentScalars)
        {
            throw new VaultException(
                VaultErrorCode.PayloadTooLarge,
                "Knowledge workflow content is too large.");
        }
    }

    internal static CreateDraftInput CreateDraftInput(
        string title,
        string summary,
        KnowledgeTypeDefinition type,
        string content,
        JsonElement properties,
        IReadOnlyList<SourceReferenceInput> sourceReferences)
    {
        if (title.Length is < 1 or > 200)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Knowledge workflow title is too long.");
        }
        EnsureContentLength(content);
        var payload = JsonSerializer.SerializeToElement(
            new KnowledgeMutationPayload(
                title,
                summary,
                type.TypeId,
                null,
                content,
                properties,
                [],
                sourceReferences),
            JsonOptions);
        return new CreateDraftInput(
            DraftTargetObjectType.Knowledge,
            DraftOperationType.Create,
            null,
            null,
            payload);
    }
}
