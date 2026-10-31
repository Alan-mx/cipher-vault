using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Documents;

internal sealed record ParsedDocumentInput(
    string Title,
    string TitleKey,
    NormalizedVirtualPath Path,
    string Format,
    string Content,
    string FrontMatterJson,
    string HeadingsJson,
    IReadOnlyList<NormalizedTag> Tags,
    IReadOnlyList<ParsedWikiLink> WikiLinks);

internal static class DocumentContentParser
{
    internal const int MaxContentBytes = 256 * 1024;
    internal const int MaxDocumentBytes = 4 * 1024 * 1024;

    internal static ParsedDocumentInput Parse(
        string title,
        string? virtualPath,
        string? folder,
        VaultDocumentFormat format,
        string content,
        IReadOnlyList<string>? explicitTags)
    {
        if (virtualPath is not null && folder is not null)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "VirtualPath and Folder cannot both be provided.");
        }

        var normalizedTitle = TitleNormalizer.NormalizeTitle(title);
        var normalizedPath = virtualPath is null
            ? VirtualPath.Build(normalizedTitle, folder, format)
            : VirtualPath.Normalize(virtualPath, format);
        var contentBytes = Encoding.UTF8.GetByteCount(content);
        if (contentBytes > MaxContentBytes)
        {
            throw new VaultException(
                VaultErrorCode.DocumentTooLarge,
                "Document content exceeds the per-write limit.");
        }

        var isMarkdown = format == VaultDocumentFormat.Markdown;
        FrontMatterSnapshot? frontMatter = null;
        if (isMarkdown
            && FrontMatterDocument.TryParse(content, out var parsedFrontMatter))
        {
            frontMatter = parsedFrontMatter;
        }

        var frontMatterJson = frontMatter is not null
            ? SerializeFrontMatter(frontMatter)
            : "{}";
        var headingsJson = isMarkdown
            ? MarkdownHeadings.Extract(content)
            : "[]";
        var tags = ResolveTags(
            format,
            content,
            explicitTags);
        var links = isMarkdown
            ? WikiLinkParser.Parse(content)
            : [];

        return new ParsedDocumentInput(
            normalizedTitle,
            TitleNormalizer.NormalizeKey(normalizedTitle),
            normalizedPath,
            format.ToDatabaseValue(),
            content,
            frontMatterJson,
            headingsJson,
            tags,
            links);
    }

    internal static IReadOnlyList<NormalizedTag> ResolveTags(
        VaultDocumentFormat format,
        string content,
        IReadOnlyList<string>? explicitTags)
    {
        if (format == VaultDocumentFormat.Markdown
            && FrontMatterTags.TryExtract(content, out var frontMatterTags))
        {
            var normalizedFrontMatterTags = TagNormalizer.NormalizeMany(frontMatterTags);
            if (explicitTags is not null)
            {
                var normalizedExplicitTags = TagNormalizer.NormalizeMany(explicitTags);
                if (!normalizedExplicitTags
                        .Select(tag => tag.NormalizedKey)
                        .SequenceEqual(
                            normalizedFrontMatterTags.Select(tag => tag.NormalizedKey),
                            StringComparer.Ordinal))
                {
                    throw new VaultException(
                        VaultErrorCode.InvalidInput,
                        "Explicit tags must match Markdown Frontmatter tags.");
                }
            }

            return normalizedFrontMatterTags;
        }

        return TagNormalizer.NormalizeMany(explicitTags);
    }

    internal static string SerializeFrontMatter(FrontMatterSnapshot snapshot)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in snapshot.Fields)
        {
            values[field.Key] = field.Value.Clone();
        }

        return JsonSerializer.Serialize(values);
    }
}
