using System.Text;

namespace CipherVault.Core.Documents;

internal static class DocumentLinkKeys
{
    public static string NormalizeTitle(string title)
    {
        return NormalizeText(title);
    }

    public static string NormalizeHeading(string heading)
    {
        return NormalizeText(heading);
    }

    public static string NormalizeWikiTarget(string rawTarget)
    {
        var normalized = rawTarget
            .Normalize(NormalizationForm.FormC)
            .Replace('\\', '/')
            .Trim()
            .Trim('/');
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^3];
        }
        return NormalizeText(normalized);
    }

    public static string NormalizeDocumentPath(string sourcePath)
    {
        var normalized = sourcePath
            .Normalize(NormalizationForm.FormC)
            .Replace('\\', '/')
            .Trim('/');
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }
        return NormalizeText(normalized);
    }

    private static string NormalizeText(string value)
    {
        return string.Join(
            ' ',
            value
                .Normalize(NormalizationForm.FormC)
                .Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }
}
