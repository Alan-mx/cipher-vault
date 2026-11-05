using System.Text.Json;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

internal static class DraftFieldDiffBuilder
{
    private const int MaxChanges = 200;
    private const int MaxValueScalars = 2_000;
    private const int MaxDepth = 8;

    private static readonly string[] KnowledgeFields =
    [
        "title",
        "summary",
        "knowledgeTypeId",
        "templateId",
        "contentMarkdown",
        "properties",
        "tags"
    ];

    private static readonly string[] SourceFields =
    [
        "sourceKind",
        "title",
        "metadata",
        "contentFormat",
        "contentText"
    ];

    private static readonly string[] RelationFields =
    [
        "relationTypeId",
        "sourceKnowledgeId",
        "targetKnowledgeId",
        "sourceKnowledgeRevision",
        "targetKnowledgeRevision",
        "rationale"
    ];

    private static readonly HashSet<string> SnapshotBaselineFields =
    [
        "templateId",
        "contentMarkdown",
        "sourceKind",
        "metadata",
        "contentFormat",
        "contentText",
        "sourceKnowledgeRevision",
        "targetKnowledgeRevision"
    ];

    internal static (
        IReadOnlyList<DraftFieldChange> Changes,
        bool Truncated) Build(DraftRecord record)
    {
        if (record.OperationType is not (
                DraftOperationType.Create or DraftOperationType.Update))
        {
            return ([], false);
        }
        var fields = GetEditableFields(record.TargetObjectType);
        if (record.OperationType == DraftOperationType.Create)
        {
            return BuildCreate(record.Payload, fields);
        }
        return BuildUpdate(record.Payload, record.BaseSnapshot, fields);
    }

    private static (
        IReadOnlyList<DraftFieldChange> Changes,
        bool Truncated) BuildCreate(
        JsonElement payload,
        IReadOnlyList<string> fields)
    {
        var after = Flatten(payload, fields);
        var changes = new List<DraftFieldChange>();
        foreach (var item in after)
        {
            AddChange(
                changes,
                new DraftFieldChange(
                    item.Path,
                    DraftFieldChangeKind.New,
                    null,
                    Format(item.Value, out var truncated),
                    ValueType(item.Value),
                    false,
                    truncated));
        }
        return Limit(changes);
    }

    private static (
        IReadOnlyList<DraftFieldChange> Changes,
        bool Truncated) BuildUpdate(
        JsonElement payload,
        JsonElement? baseSnapshot,
        IReadOnlyList<string> fields)
    {
        var after = Flatten(payload, fields);
        var before = baseSnapshot is { } snapshot
            ? Flatten(snapshot, fields)
            : [];
        var beforeByPath = before.ToDictionary(
            item => item.Path,
            item => item.Value,
            StringComparer.Ordinal);
        var afterByPath = after.ToDictionary(
            item => item.Path,
            item => item.Value,
            StringComparer.Ordinal);
        var changes = new List<DraftFieldChange>();

        foreach (var item in after)
        {
            if (beforeByPath.TryGetValue(item.Path, out var oldValue))
            {
                if (JsonElement.DeepEquals(oldValue, item.Value))
                {
                    continue;
                }
                AddChange(
                    changes,
                    CreateChange(
                        item.Path,
                        DraftFieldChangeKind.Modified,
                        oldValue,
                        item.Value));
                continue;
            }
            var kind = baseSnapshot is null
                || SnapshotBaselineFields.Contains(item.Path.Split('.')[0])
                ? DraftFieldChangeKind.BaselineUnavailable
                : DraftFieldChangeKind.Added;
            AddChange(
                changes,
                CreateChange(item.Path, kind, null, item.Value));
        }

        foreach (var item in before)
        {
            if (afterByPath.ContainsKey(item.Path))
            {
                continue;
            }
            AddChange(
                changes,
                CreateChange(
                    item.Path,
                    DraftFieldChangeKind.Removed,
                    item.Value,
                    null));
        }
        return Limit(changes);
    }

    private static DraftFieldChange CreateChange(
        string path,
        DraftFieldChangeKind kind,
        JsonElement? before,
        JsonElement? after)
    {
        var value = after ?? before;
        var beforeTruncated = false;
        var afterTruncated = false;
        return new DraftFieldChange(
            path,
            kind,
            before is { } oldValue
                ? Format(oldValue, out beforeTruncated)
                : null,
            after is { } newValue
                ? Format(newValue, out afterTruncated)
                : null,
            value is { } valueElement
                ? ValueType(valueElement)
                : "null",
            beforeTruncated,
            afterTruncated);
    }

    private static void AddChange(
        List<DraftFieldChange> changes,
        DraftFieldChange change) =>
        changes.Add(change);

    private static (
        IReadOnlyList<DraftFieldChange> Changes,
        bool Truncated) Limit(List<DraftFieldChange> changes) =>
        changes.Count > MaxChanges
            ? (changes.Take(MaxChanges).ToArray(), true)
            : (changes, false);

    private static List<FlatValue> Flatten(
        JsonElement element,
        IReadOnlyList<string> fields)
    {
        var values = new List<FlatValue>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return values;
        }
        foreach (var field in fields)
        {
            if (element.TryGetProperty(field, out var value))
            {
                FlattenValue(values, field, value, 1);
            }
        }
        return values;
    }

    private static void FlattenValue(
        List<FlatValue> values,
        string path,
        JsonElement value,
        int depth)
    {
        if (value.ValueKind == JsonValueKind.Object
            && depth < MaxDepth
            && value.EnumerateObject().Any())
        {
            foreach (var property in value.EnumerateObject())
            {
                FlattenValue(
                    values,
                    $"{path}.{property.Name}",
                    property.Value,
                    depth + 1);
            }
            return;
        }
        values.Add(new FlatValue(path, value.Clone()));
    }

    private static string Format(
        JsonElement value,
        out bool truncated)
    {
        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
        truncated = text.EnumerateRunes().Count() > MaxValueScalars;
        if (!truncated)
        {
            return text;
        }
        return string.Concat(
            text.EnumerateRunes()
                .Take(MaxValueScalars)
                .Select(rune => rune.ToString())) + "…";
    }

    private static string ValueType(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            _ => "null"
        };

    private static IReadOnlyList<string> GetEditableFields(
        DraftTargetObjectType targetType) =>
        targetType switch
        {
            DraftTargetObjectType.Knowledge => KnowledgeFields,
            DraftTargetObjectType.Source => SourceFields,
            DraftTargetObjectType.Relation => RelationFields,
            _ => []
        };

    private sealed record FlatValue(
        string Path,
        JsonElement Value);
}
