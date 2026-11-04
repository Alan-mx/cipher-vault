using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Server.Api;
using CipherVault.Server.Mcp.Contracts;

namespace CipherVault.Server.Mcp;

public static class McpDocumentMapProjector
{
    private const int MaxHeadings = 500;
    private const int MaxFrontMatterFields = 100;
    private const int MaxLinks = 500;
    private const int MaxCandidates = 50;
    private const int MaxHeadingText = 1000;
    private const int MaxHeadingPathSegments = 32;
    private const int MaxHeadingPathSegment = 256;
    private const int MaxFrontMatterKey = 256;
    private const int MaxFrontMatterValue = 4096;
    private const int MaxLinkText = 512;
    private const int MaxSerializedBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions =
        ApiJson.CreateOptions();

    public static DocumentMapToolResult Project(DocumentMap map)
    {
        var truncatedFields = new List<string>();
        var headings = map.Headings
            .Take(MaxHeadings)
            .Select(heading => new DocumentHeading(
                heading.Level,
                Truncate(heading.Text, MaxHeadingText, truncatedFields, "headings.text"),
                TruncatePath(heading.Path, truncatedFields)))
            .ToArray();
        var frontMatter = map.FrontMatterFields
            .Take(MaxFrontMatterFields)
            .Select(field => new FrontMatterFieldView(
                Truncate(
                    field.Key,
                    MaxFrontMatterKey,
                    truncatedFields,
                    "frontMatter.key"),
                field.ValueType,
                Truncate(
                    field.ValueJson,
                    MaxFrontMatterValue,
                    truncatedFields,
                    "frontMatter.value")))
            .ToArray();
        var links = map.OutgoingLinks
            .Take(MaxLinks)
            .Select(link => new OutgoingLinkView(
                link.LinkKind,
                Truncate(link.RawTarget, MaxLinkText, truncatedFields, "links.text"),
                Truncate(link.DisplayText, MaxLinkText, truncatedFields, "links.text"),
                link.TargetHeading is null
                    ? null
                    : Truncate(
                        link.TargetHeading,
                        MaxLinkText,
                        truncatedFields,
                        "links.text"),
                link.ResolutionStatus,
                ProjectTarget(link.Target, truncatedFields),
                link.Candidates
                    .Take(MaxCandidates)
                    .Select(candidate => ProjectTarget(candidate, truncatedFields)!)
                    .ToArray(),
                link.StartOffset,
                link.EndOffset,
                link.DestinationStartOffset,
                link.DestinationEndOffset))
            .ToArray();

        if (map.Headings.Count > MaxHeadings)
        {
            AddTruncated(truncatedFields, "headings.count");
        }
        if (map.FrontMatterFields.Count > MaxFrontMatterFields)
        {
            AddTruncated(truncatedFields, "frontMatter.count");
        }
        if (map.OutgoingLinks.Count > MaxLinks)
        {
            AddTruncated(truncatedFields, "links.count");
        }
        if (map.OutgoingLinks.Any(link => link.Candidates.Count > MaxCandidates))
        {
            AddTruncated(truncatedFields, "links.candidates");
        }

        var result = new DocumentMapToolResult(
            CreateMap(
                map,
                headings,
                frontMatter,
                links,
                truncatedFields),
            truncatedFields.Count > 0,
            truncatedFields.ToArray());

        while (SerializedSize(result) > MaxSerializedBytes)
        {
            if (links.Length > 0)
            {
                links = links[..^1];
                AddTruncated(truncatedFields, "links.count");
            }
            else if (frontMatter.Length > 0)
            {
                frontMatter = frontMatter[..^1];
                AddTruncated(truncatedFields, "frontMatter.count");
            }
            else if (headings.Length > 0)
            {
                headings = headings[..^1];
                AddTruncated(truncatedFields, "headings.count");
            }
            else
            {
                throw new McpToolRequestException(
                    "internal_error",
                    "Document map metadata exceeds the response limit.");
            }

            result = new DocumentMapToolResult(
                CreateMap(
                    map,
                    headings,
                    frontMatter,
                    links,
                    truncatedFields),
                ResponseTruncated: true,
                TruncatedFields: truncatedFields.ToArray());
        }

        return result with
        {
            ResponseTruncated = result.ResponseTruncated ||
                result.Map.HeadingsTruncated ||
                result.Map.FrontMatterTruncated ||
                result.Map.LinksTruncated
        };
    }

    private static DocumentMap CreateMap(
        DocumentMap source,
        IReadOnlyList<DocumentHeading> headings,
        IReadOnlyList<FrontMatterFieldView> frontMatter,
        IReadOnlyList<OutgoingLinkView> links,
        IReadOnlyCollection<string> truncatedFields) =>
        source with
        {
            Headings = headings.ToArray(),
            FrontMatterFields = frontMatter.ToArray(),
            OutgoingLinks = links.ToArray(),
            HeadingsTruncated = source.HeadingsTruncated
                || headings.Count < source.Headings.Count,
            FrontMatterTruncated = source.FrontMatterTruncated
                || frontMatter.Count < source.FrontMatterFields.Count,
            LinksTruncated = source.LinksTruncated
                || links.Count < source.OutgoingLinks.Count
        };

    private static LinkTargetView? ProjectTarget(
        LinkTargetView? target,
        ICollection<string> truncatedFields)
    {
        if (target is null)
        {
            return null;
        }

        return new LinkTargetView(
            target.DocumentId,
            Truncate(target.Title, MaxLinkText, truncatedFields, "links.text"),
            target.VirtualPath);
    }

    private static IReadOnlyList<string> TruncatePath(
        IReadOnlyList<string> path,
        ICollection<string> truncatedFields)
    {
        if (path.Count > MaxHeadingPathSegments)
        {
            AddTruncated(truncatedFields, "headings.path");
        }

        return path
            .Take(MaxHeadingPathSegments)
            .Select(segment => Truncate(
                segment,
                MaxHeadingPathSegment,
                truncatedFields,
                "headings.path"))
            .ToArray();
    }

    private static string Truncate(
        string value,
        int maxLength,
        ICollection<string> truncatedFields,
        string field)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        AddTruncated(truncatedFields, field);
        var length = maxLength;
        if (length > 0
            && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }
        return value[..length];
    }

    private static void AddTruncated(
        ICollection<string> fields,
        string field)
    {
        if (!fields.Contains(field))
        {
            fields.Add(field);
        }
    }

    private static int SerializedSize(DocumentMapToolResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length;
}
