using CipherVault.Core.Documents;

namespace CipherVault.Host.Mcp;

public static class BridgeDocumentMapLimiter
{
    private const int MaxItems = 500;

    public static VaultDocumentMap Apply(
        VaultDocumentMap map,
        int? maxHeadings,
        int? maxFrontMatterFields,
        int? maxLinks)
    {
        return map with
        {
            Headings = Take(map.Headings, maxHeadings, out var headingsTruncated),
            FrontMatterFields = Take(
                map.FrontMatterFields,
                maxFrontMatterFields,
                out var frontMatterTruncated),
            OutgoingLinks = Take(
                map.OutgoingLinks,
                maxLinks,
                out var linksTruncated),
            HeadingsTruncated = headingsTruncated,
            FrontMatterTruncated = frontMatterTruncated,
            LinksTruncated = linksTruncated
        };
    }

    public static bool IsValidLimit(int? value)
    {
        return value is null or >= 0 and <= MaxItems;
    }

    private static IReadOnlyList<T> Take<T>(
        IReadOnlyList<T> values,
        int? limit,
        out bool truncated)
    {
        if (limit is null)
        {
            truncated = false;
            return values;
        }
        var effectiveLimit = Math.Clamp(limit.Value, 0, MaxItems);
        truncated = values.Count > effectiveLimit;
        return truncated
            ? values.Take(effectiveLimit).ToArray()
            : values;
    }
}
