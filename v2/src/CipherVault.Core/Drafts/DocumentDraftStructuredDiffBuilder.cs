using System.Text;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Drafts;

internal static class DocumentDraftStructuredDiffBuilder
{
    private const int MaxValueScalars = 2_000;
    private const int MaxChanges = 100;

    internal static DocumentDraftStructuredChanges Build(
        DocumentDraftType draftType,
        DocumentDraftSnapshot? baseSnapshot,
        DocumentDraftWorkingCopy preview)
    {
        if (draftType == DocumentDraftType.Trash)
        {
            return new DocumentDraftStructuredChanges(
                [
                    new DocumentDraftFieldChange(
                        "trash",
                        null,
                        BeforeExists: true,
                        BeforeValueType: "boolean",
                        Before: "true",
                        BeforeTruncated: false,
                        AfterExists: false,
                        AfterValueType: null,
                        After: null,
                        AfterTruncated: false)
                ],
                TotalChangeCount: 1,
                Truncated: false);
        }

        var builder = new ChangeAccumulator();
        var beforeExists = draftType != DocumentDraftType.Create && baseSnapshot is not null;
        AddScalar(
            builder,
            "title",
            null,
            beforeExists,
            baseSnapshot?.Title,
            preview.Title);
        AddScalar(
            builder,
            "virtual_path",
            null,
            beforeExists,
            baseSnapshot?.VirtualPath,
            preview.VirtualPath);
        AddScalar(
            builder,
            "format",
            null,
            beforeExists,
            baseSnapshot is null ? null : Format(baseSnapshot.Format),
            Format(preview.Format));

        var beforeTags = baseSnapshot?.Tags ?? [];
        if (draftType == DocumentDraftType.Create
            || !TagKeys(beforeTags).SequenceEqual(
                TagKeys(preview.Tags),
                StringComparer.Ordinal))
        {
            AddValue(
                builder,
                "tags",
                null,
                beforeExists,
                "array",
                TagsJson(beforeTags),
                afterExists: true,
                "array",
                TagsJson(preview.Tags));
        }

        var beforeFrontMatter = ReadFrontMatter(baseSnapshot?.Content, baseSnapshot?.Format);
        var afterFrontMatter = ReadFrontMatter(preview.Content, preview.Format);
        foreach (var key in beforeFrontMatter.Keys
                     .Concat(afterFrontMatter.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .Where(key => !string.Equals(
                         key,
                         "tags",
                         StringComparison.Ordinal))
                     .OrderBy(key => key, StringComparer.Ordinal))
        {
            var hasBefore = beforeFrontMatter.TryGetValue(key, out var before);
            var hasAfter = afterFrontMatter.TryGetValue(key, out var after);
            if (!hasBefore && !hasAfter)
            {
                continue;
            }
            if (hasBefore
                && hasAfter
                && string.Equals(
                    CanonicalJsonText.Serialize(before),
                    CanonicalJsonText.Serialize(after),
                    StringComparison.Ordinal))
            {
                continue;
            }
            AddValue(
                builder,
                "frontmatter",
                key,
                hasBefore,
                hasBefore ? JsonType(before) : null,
                hasBefore ? JsonDisplay(before) : null,
                hasAfter,
                hasAfter ? JsonType(after) : null,
                hasAfter ? JsonDisplay(after) : null);
        }

        AddScalar(
            builder,
            "body",
            null,
            beforeExists,
            baseSnapshot is null
                ? null
                : ReadBody(baseSnapshot.Content, baseSnapshot.Format),
            ReadBody(preview.Content, preview.Format));

        return builder.Build();
    }

    private static void AddScalar(
        ChangeAccumulator builder,
        string kind,
        string? key,
        bool beforeExists,
        string? before,
        string after)
    {
        if (beforeExists
            && string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }
        AddValue(
            builder,
            kind,
            key,
            beforeExists,
            beforeExists ? "string" : null,
            before,
            afterExists: true,
            "string",
            after);
    }

    private static void AddValue(
        ChangeAccumulator builder,
        string kind,
        string? key,
        bool beforeExists,
        string? beforeType,
        string? before,
        bool afterExists,
        string? afterType,
        string? after)
    {
        var boundedBefore = Bound(before);
        var boundedAfter = Bound(after);
        builder.Add(new DocumentDraftFieldChange(
            kind,
            key,
            beforeExists,
            beforeExists ? beforeType : null,
            beforeExists ? boundedBefore.Text : null,
            beforeExists && boundedBefore.Truncated,
            afterExists,
            afterExists ? afterType : null,
            afterExists ? boundedAfter.Text : null,
            afterExists && boundedAfter.Truncated));
    }

    private static Dictionary<string, JsonElement> ReadFrontMatter(
        string? content,
        VaultDocumentFormat? format)
    {
        if (format != VaultDocumentFormat.Markdown
            || string.IsNullOrEmpty(content)
            || !FrontMatterDocument.TryParse(content, out var snapshot))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        return snapshot.Fields.ToDictionary(
            field => field.Key,
            field => field.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static string ReadBody(
        string content,
        VaultDocumentFormat format)
    {
        if (format != VaultDocumentFormat.Markdown
            || !FrontMatterDocument.TryParse(content, out var snapshot))
        {
            return content;
        }
        return content[snapshot.End..];
    }

    private static IReadOnlyList<string> TagKeys(
        IReadOnlyList<string> tags) =>
        TagNormalizer.NormalizeMany(tags)
            .Select(tag => tag.NormalizedKey)
            .ToArray();

    private static string TagsJson(IReadOnlyList<string> tags)
    {
        var values = TagNormalizer.NormalizeMany(tags)
            .OrderBy(tag => tag.NormalizedKey, StringComparer.Ordinal)
            .Select(tag => tag.Name)
            .ToArray();
        return JsonSerializer.Serialize(values);
    }

    private static string Format(VaultDocumentFormat format) =>
        DocumentDraftPayloadEngine.FormatToDatabaseValue(format);

    private static string JsonType(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            JsonValueKind.Null => "null",
            _ => throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Draft Frontmatter contains an invalid JSON value.")
        };

    private static string JsonDisplay(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or
            JsonValueKind.True or
            JsonValueKind.False or
            JsonValueKind.Null => value.GetRawText(),
            JsonValueKind.Array or JsonValueKind.Object =>
                CanonicalJsonText.Serialize(value),
            _ => string.Empty
        };

    private static (string Text, bool Truncated) Bound(string? value)
    {
        if (value is null)
        {
            return (string.Empty, false);
        }
        var runes = value.EnumerateRunes().ToArray();
        if (runes.Length <= MaxValueScalars)
        {
            return (value, false);
        }
        var builder = new StringBuilder();
        foreach (var rune in runes.Take(MaxValueScalars))
        {
            builder.Append(rune);
        }
        return (builder.ToString(), true);
    }

    private sealed class ChangeAccumulator
    {
        private readonly List<DocumentDraftFieldChange> changes = [];
        private int total;

        internal void Add(DocumentDraftFieldChange change)
        {
            total++;
            if (changes.Count < MaxChanges)
            {
                changes.Add(change);
            }
        }

        internal DocumentDraftStructuredChanges Build() =>
            new(changes, total, total > MaxChanges);
    }
}
