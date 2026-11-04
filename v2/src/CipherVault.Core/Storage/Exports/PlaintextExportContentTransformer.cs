using CipherVault.Core.Documents;
using CipherVault.Core.Exports;

namespace CipherVault.Core.Storage.Exports;

internal static class PlaintextExportContentTransformer
{
    internal static string Transform(
        string content,
        string sourceVirtualPath,
        IReadOnlyList<ExportDocumentLink> links)
    {
        var result = content;
        foreach (var link in links
                     .Where(link =>
                         link.Kind == DocumentLinkKind.Wiki
                         && string.Equals(
                             link.ResolutionStatus,
                             "resolved",
                             StringComparison.Ordinal)
                         && link.TargetVirtualPath is not null)
                     .OrderByDescending(link => link.StartOffset))
        {
            if (link.StartOffset < 0
                || link.EndOffset <= link.StartOffset
                || link.EndOffset > result.Length)
            {
                throw new InvalidOperationException(
                    "Exported document link offsets are invalid.");
            }
            var destination = MarkdownLinkWriter.BuildRelativeDestination(
                sourceVirtualPath,
                link.TargetVirtualPath!,
                link.TargetHeading);
            result = string.Concat(
                result.AsSpan(0, link.StartOffset),
                $"[{EscapeLinkText(link.DisplayText)}]({destination})",
                result.AsSpan(link.EndOffset));
        }
        return result;
    }

    private static string EscapeLinkText(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}
