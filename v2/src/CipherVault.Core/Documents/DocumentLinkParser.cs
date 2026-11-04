namespace CipherVault.Core.Documents;

internal static class DocumentLinkParser
{
    private const int MaxRawTargetLength = 4096;
    private const int MaxRawTargetKeyLength = 512;
    private const int MaxDisplayTextLength = 512;
    private const int MaxHeadingLength = 256;

    internal static IReadOnlyList<ParsedDocumentLink> Parse(
        string content,
        NormalizedVirtualPath sourcePath)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var protectedRanges = WikiLinkParser.FindProtectedRanges(content);
        var wikiLinks = WikiLinkParser.Parse(content, protectedRanges);
        var wikiRanges = wikiLinks
            .Select(link => new WikiLinkParser.TextRange(
                link.StartOffset,
                link.EndOffset))
            .ToArray();
        var links = new List<ParsedDocumentLink>(
            wikiLinks.Count + 16);
        foreach (var link in wikiLinks)
        {
            if (link.RawTarget.Length > MaxRawTargetLength
                || link.DisplayText.Length > MaxDisplayTextLength)
            {
                continue;
            }

            var rawTargetKey = WikiLinkParser.NormalizeRawTargetKey(
                link.RawTarget);
            if (rawTargetKey.Length == 0
                || rawTargetKey.Length > MaxRawTargetKeyLength)
            {
                continue;
            }
            var targetHeadingKey = link.TargetHeading is null
                ? null
                : MarkdownLinkPathResolver.NormalizeHeadingKey(
                    link.TargetHeading);
            if ((link.TargetHeading?.Length > MaxHeadingLength)
                || (targetHeadingKey?.Length > MaxHeadingLength))
            {
                continue;
            }

            links.Add(new ParsedDocumentLink(
                DocumentLinkKind.Wiki,
                link.RawTarget,
                rawTargetKey,
                null,
                link.DisplayText,
                link.TargetHeading,
                targetHeadingKey,
                link.StartOffset,
                link.EndOffset,
                null,
                null));
        }

        links.AddRange(
            MarkdownLinkParser.Parse(
                content,
                sourcePath,
                protectedRanges,
                wikiRanges));
        return links
            .OrderBy(link => link.StartOffset)
            .ThenBy(link => link.Kind == DocumentLinkKind.Wiki ? 0 : 1)
            .DistinctBy(link => link.StartOffset)
            .ToArray();
    }
}
