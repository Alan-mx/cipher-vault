using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Host.Mcp;

public static class BridgeDocumentMapLimiter
{
    private const int MaxItems = 500;
    private const int MaxBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static VaultDocumentMap Apply(
        VaultDocumentMap map,
        int? maxHeadings,
        int? maxFrontMatterFields,
        int? maxLinks,
        int? maxResponseBytes)
    {
        var limited = map with
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
        var estimatedBytes = EstimateBytes(limited);
        if (maxResponseBytes is null)
        {
            return limited with { EstimatedBytes = estimatedBytes };
        }

        var byteLimit = Math.Clamp(maxResponseBytes.Value, 0, MaxBytes);
        var bytesTruncated = estimatedBytes > byteLimit;
        while (estimatedBytes > byteLimit)
        {
            if (limited.OutgoingLinks.Count > 0)
            {
                limited = limited with
                {
                    OutgoingLinks = RemoveLast(limited.OutgoingLinks),
                    LinksTruncated = true
                };
            }
            else if (limited.FrontMatterFields.Count > 0)
            {
                limited = limited with
                {
                    FrontMatterFields = RemoveLast(limited.FrontMatterFields),
                    FrontMatterTruncated = true
                };
            }
            else if (limited.Headings.Count > 0)
            {
                limited = limited with
                {
                    Headings = RemoveLast(limited.Headings),
                    HeadingsTruncated = true
                };
            }
            else
            {
                break;
            }
            bytesTruncated = true;
            estimatedBytes = EstimateBytes(limited);
        }
        return limited with
        {
            EstimatedBytes = estimatedBytes,
            BytesTruncated = bytesTruncated
        };
    }

    public static bool IsValidLimit(int? value)
    {
        return value is null or >= 0 and <= MaxItems;
    }

    public static bool IsValidByteLimit(int? value)
    {
        return value is null or >= 0 and <= MaxBytes;
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

    private static IReadOnlyList<T> RemoveLast<T>(IReadOnlyList<T> values)
    {
        return values.Take(values.Count - 1).ToArray();
    }

    private static int EstimateBytes(VaultDocumentMap map)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                map.DocumentId,
                map.Title,
                map.VirtualPath,
                map.Format,
                map.Revision,
                map.Headings,
                map.FrontMatterFields,
                map.OutgoingLinks,
                map.BacklinkCount,
                map.UpdatedAt,
                map.HeadingsTruncated,
                map.FrontMatterTruncated,
                map.LinksTruncated
            },
            JsonOptions).Length;
    }
}
