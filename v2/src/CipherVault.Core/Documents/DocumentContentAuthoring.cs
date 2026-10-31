using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Documents;

public static class DocumentContentAuthoring
{
    public static string RewriteFrontMatterTags(
        string content,
        IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(tags);
        var normalized = TagNormalizer.NormalizeMany(tags);
        var values = normalized
            .Select(tag => tag.Name)
            .ToArray();
        if (!FrontMatterDocument.TryParse(content, out var snapshot))
        {
            return PrependFrontMatter(content, values);
        }

        var matchingFields = snapshot.Fields
            .Where(field => string.Equals(
                field.Key,
                "tags",
                StringComparison.Ordinal))
            .ToArray();
        if (matchingFields.Length > 1)
        {
            throw new VaultException(
                VaultErrorCode.FrontMatterInvalid,
                "The frontmatter tags key is duplicated.");
        }

        var json = JsonSerializer.SerializeToElement(values);
        if (matchingFields.Length == 1)
        {
            return DocumentPatchEngine.Apply(
                content,
                new VaultPatchCommand(
                    "frontmatter",
                    null,
                    "tags",
                    "replace",
                    null,
                    json,
                    DryRun: false)).Content;
        }

        var newline = DetectNewLine(content);
        var insertion = RenderTags(values, newline);
        var updated = string.Concat(
            content.AsSpan(0, snapshot.BodyStart),
            insertion,
            content.AsSpan(snapshot.BodyStart));
        _ = FrontMatterDocument.TryParse(updated, out _);
        return updated;
    }

    public static IReadOnlyList<string> ResolveEffectiveTags(
        VaultDocumentFormat format,
        string content,
        IReadOnlyList<string>? fallbackTags)
    {
        var hasAuthoritativeMarkdownTags =
            format == VaultDocumentFormat.Markdown
            && FrontMatterTags.TryExtract(content, out _);
        return DocumentContentParser.ResolveTags(
                format,
                content,
                hasAuthoritativeMarkdownTags ? null : fallbackTags)
            .Select(tag => tag.Name)
            .ToArray();
    }

    private static string PrependFrontMatter(
        string content,
        IReadOnlyList<string> tags)
    {
        var newline = DetectNewLine(content);
        var block = string.Concat(
            "---",
            newline,
            RenderTags(tags, newline),
            "---",
            newline,
            content);
        _ = FrontMatterDocument.TryParse(block, out _);
        return block;
    }

    private static string RenderTags(
        IReadOnlyList<string> tags,
        string newline)
    {
        if (tags.Count == 0)
        {
            return $"tags: []{newline}";
        }

        return string.Join(
            newline,
            new[] { "tags:" }
                .Concat(tags.Select(tag =>
                    $"  - {JsonSerializer.Serialize(tag)}"))
                .Append(string.Empty));
    }

    private static string DetectNewLine(string content) =>
        content.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
}
