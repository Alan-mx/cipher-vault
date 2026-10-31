namespace CipherVault.Core.Documents;

internal static class UnlinkedMentionScanner
{
    private const int ContextRadius = 80;

    public static UnlinkedMentionScanResult Scan(
        string content,
        string title,
        int maxOccurrences)
    {
        var term = title.Trim();
        if (term.Length == 0 || maxOccurrences < 1)
        {
            return new UnlinkedMentionScanResult(0, []);
        }

        var bodyStart = FrontMatterDocument.TryParse(content, out var snapshot)
            ? snapshot.End
            : 0;
        var protectedRanges = WikiLinkParser.FindProtectedRanges(content);
        var protectedAndLinkedRanges = protectedRanges
            .Concat(WikiLinkParser.Parse(content, protectedRanges).Select(link =>
                new WikiLinkParser.TextRange(
                    link.StartOffset,
                    link.EndOffset)))
            .OrderBy(range => range.Start)
            .ToArray();
        var occurrenceCount = 0;
        var occurrences = new List<VaultUnlinkedMentionOccurrence>();
        var cursor = bodyStart;
        while (cursor <= content.Length - term.Length)
        {
            var start = content.IndexOf(
                term,
                cursor,
                StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }
            var end = start + term.Length;
            if (!IsProtected(start, end, protectedAndLinkedRanges) &&
                HasWordBoundaries(content, term, start, end))
            {
                occurrenceCount++;
                if (occurrences.Count < maxOccurrences)
                {
                    occurrences.Add(new VaultUnlinkedMentionOccurrence(
                        start,
                        end,
                        BuildContext(content, bodyStart, start, end)));
                }
            }
            cursor = end;
        }

        return new UnlinkedMentionScanResult(
            occurrenceCount,
            occurrences);
    }

    private static bool IsProtected(
        int start,
        int end,
        IReadOnlyList<WikiLinkParser.TextRange> ranges)
    {
        foreach (var range in ranges)
        {
            if (range.Start < end && range.End > start)
            {
                return true;
            }
            if (range.Start >= end)
            {
                break;
            }
        }
        return false;
    }

    private static bool HasWordBoundaries(
        string content,
        string term,
        int start,
        int end)
    {
        if (IsAsciiWord(term[0]) &&
            start > 0 &&
            IsAsciiWord(content[start - 1]))
        {
            return false;
        }
        return !IsAsciiWord(term[^1]) ||
            end >= content.Length ||
            !IsAsciiWord(content[end]);
    }

    private static bool IsAsciiWord(char value)
    {
        return value is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '_';
    }

    private static string BuildContext(
        string content,
        int bodyStart,
        int start,
        int end)
    {
        var contextStart = Math.Max(bodyStart, start - ContextRadius);
        var contextEnd = Math.Min(content.Length, end + ContextRadius);
        var excerpt = string.Join(
            ' ',
            content[contextStart..contextEnd].Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        return string.Concat(
            contextStart > bodyStart ? "... " : string.Empty,
            excerpt,
            contextEnd < content.Length ? " ..." : string.Empty);
    }
}

internal sealed record UnlinkedMentionScanResult(
    int OccurrenceCount,
    IReadOnlyList<VaultUnlinkedMentionOccurrence> Occurrences);
