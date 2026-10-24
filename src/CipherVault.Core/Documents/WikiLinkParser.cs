namespace CipherVault.Core.Documents;

internal static class WikiLinkParser
{
    public static IReadOnlyList<ParsedWikiLink> Parse(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var protectedRanges = FindProtectedRanges(content);
        var rangeIndex = 0;
        var links = new List<ParsedWikiLink>();
        for (var index = 0; index < content.Length - 1; index++)
        {
            while (rangeIndex < protectedRanges.Count &&
                   protectedRanges[rangeIndex].End <= index)
            {
                rangeIndex++;
            }
            if (rangeIndex < protectedRanges.Count &&
                protectedRanges[rangeIndex].Start <= index)
            {
                index = protectedRanges[rangeIndex].End - 1;
                continue;
            }

            if (content[index] != '[' ||
                content[index + 1] != '[' ||
                IsEscaped(content, index))
            {
                continue;
            }

            var close = content.IndexOf("]]", index + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }
            var inner = content[(index + 2)..close];
            if (inner.Contains('\n') || inner.Contains('\r'))
            {
                continue;
            }
            if (!TryParseInner(inner, out var parsed))
            {
                index = close + 1;
                continue;
            }

            links.Add(parsed with
            {
                StartOffset = index,
                EndOffset = close + 2
            });
            index = close + 1;
        }
        return links;
    }

    private static bool TryParseInner(
        string inner,
        out ParsedWikiLink parsed)
    {
        parsed = default!;
        var trimmed = inner.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var separator = trimmed.IndexOf('|');
        var targetPart = separator >= 0
            ? trimmed[..separator]
            : trimmed;
        var displayText = separator >= 0
            ? trimmed[(separator + 1)..].Trim()
            : string.Empty;
        var headingSeparator = targetPart.IndexOf('#');
        var rawTarget = (headingSeparator >= 0
                ? targetPart[..headingSeparator]
                : targetPart)
            .Trim();
        if (rawTarget.Length == 0)
        {
            return false;
        }
        var targetHeading = headingSeparator >= 0
            ? targetPart[(headingSeparator + 1)..].Trim()
            : null;
        if (string.IsNullOrEmpty(targetHeading))
        {
            targetHeading = null;
        }
        if (displayText.Length == 0)
        {
            displayText = rawTarget;
        }

        parsed = new ParsedWikiLink(
            rawTarget,
            displayText,
            targetHeading,
            0,
            0);
        return true;
    }

    private static bool IsEscaped(string content, int index)
    {
        var slashCount = 0;
        for (var cursor = index - 1;
             cursor >= 0 && content[cursor] == '\\';
             cursor--)
        {
            slashCount++;
        }
        return slashCount % 2 == 1;
    }

    private static IReadOnlyList<TextRange> FindProtectedRanges(string content)
    {
        var ranges = new List<TextRange>();
        AddFencedCodeRanges(content, ranges);
        AddDelimitedRanges(content, "<!--", "-->", ranges);
        AddInlineCodeRanges(content, ranges);
        return MergeRanges(ranges);
    }

    private static void AddFencedCodeRanges(
        string content,
        ICollection<TextRange> ranges)
    {
        Fence? openFence = null;
        var lineStart = 0;
        while (lineStart < content.Length)
        {
            var newline = content.IndexOf('\n', lineStart);
            var lineEnd = newline >= 0 ? newline : content.Length;
            var line = content.AsSpan(lineStart, lineEnd - lineStart).TrimStart();
            if (openFence is null && TryReadFence(line, out var fence))
            {
                openFence = fence with { Start = lineStart };
            }
            else if (openFence is not null &&
                     TryReadFence(line, out fence) &&
                     fence.Marker == openFence.Value.Marker &&
                     fence.Length >= openFence.Value.Length)
            {
                var end = newline >= 0 ? newline + 1 : lineEnd;
                ranges.Add(new TextRange(openFence.Value.Start, end));
                openFence = null;
            }

            if (newline < 0)
            {
                break;
            }
            lineStart = newline + 1;
        }
        if (openFence is not null)
        {
            ranges.Add(new TextRange(openFence.Value.Start, content.Length));
        }
    }

    private static bool TryReadFence(
        ReadOnlySpan<char> line,
        out Fence fence)
    {
        fence = default;
        if (line.Length < 3 || (line[0] != '`' && line[0] != '~'))
        {
            return false;
        }
        var marker = line[0];
        var length = 0;
        while (length < line.Length && line[length] == marker)
        {
            length++;
        }
        if (length < 3)
        {
            return false;
        }
        fence = new Fence(marker, length, 0);
        return true;
    }

    private static void AddDelimitedRanges(
        string content,
        string open,
        string close,
        ICollection<TextRange> ranges)
    {
        var offset = 0;
        while (offset < content.Length)
        {
            var start = content.IndexOf(open, offset, StringComparison.Ordinal);
            if (start < 0)
            {
                return;
            }
            var end = content.IndexOf(close, start + open.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                ranges.Add(new TextRange(start, content.Length));
                return;
            }
            ranges.Add(new TextRange(start, end + close.Length));
            offset = end + close.Length;
        }
    }

    private static void AddInlineCodeRanges(
        string content,
        ICollection<TextRange> ranges)
    {
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] != '`' || IsEscaped(content, index))
            {
                continue;
            }
            var length = 1;
            while (index + length < content.Length &&
                   content[index + length] == '`')
            {
                length++;
            }
            var delimiter = new string('`', length);
            var close = content.IndexOf(
                delimiter,
                index + length,
                StringComparison.Ordinal);
            if (close < 0)
            {
                continue;
            }
            ranges.Add(new TextRange(index, close + length));
            index = close + length - 1;
        }
    }

    private static IReadOnlyList<TextRange> MergeRanges(
        IReadOnlyList<TextRange> ranges)
    {
        if (ranges.Count < 2)
        {
            return ranges;
        }

        var ordered = ranges
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToList();
        var merged = new List<TextRange> { ordered[0] };
        foreach (var range in ordered.Skip(1))
        {
            var current = merged[^1];
            if (range.Start <= current.End)
            {
                merged[^1] = current with
                {
                    End = Math.Max(current.End, range.End)
                };
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }

    private readonly record struct Fence(char Marker, int Length, int Start);

    private sealed record TextRange(int Start, int End);
}

internal sealed record ParsedWikiLink(
    string RawTarget,
    string DisplayText,
    string? TargetHeading,
    int StartOffset,
    int EndOffset);
