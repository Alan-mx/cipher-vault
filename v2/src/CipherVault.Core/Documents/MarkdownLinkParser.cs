namespace CipherVault.Core.Documents;

internal static class MarkdownLinkParser
{
    internal static IReadOnlyList<ParsedDocumentLink> Parse(
        string content,
        NormalizedVirtualPath sourcePath,
        IReadOnlyList<WikiLinkParser.TextRange> protectedRanges,
        IReadOnlyList<WikiLinkParser.TextRange> excludedRanges)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var links = new List<ParsedDocumentLink>();
        var protectedIndex = 0;
        var excludedIndex = 0;
        for (var index = 0; index < content.Length; index++)
        {
            while (protectedIndex < protectedRanges.Count
                   && protectedRanges[protectedIndex].End <= index)
            {
                protectedIndex++;
            }
            if (protectedIndex < protectedRanges.Count
                && protectedRanges[protectedIndex].Start <= index)
            {
                index = protectedRanges[protectedIndex].End - 1;
                continue;
            }
            while (excludedIndex < excludedRanges.Count
                   && excludedRanges[excludedIndex].End <= index)
            {
                excludedIndex++;
            }
            if (excludedIndex < excludedRanges.Count
                && excludedRanges[excludedIndex].Start <= index)
            {
                index = excludedRanges[excludedIndex].End - 1;
                continue;
            }
            if (content[index] != '['
                || WikiLinkParser.IsEscaped(content, index)
                || IsImageMarker(content, index))
            {
                continue;
            }

            var parsed = TryParseAt(content, index, sourcePath);
            if (parsed is null)
            {
                continue;
            }
            links.Add(parsed);
            index = parsed.EndOffset - 1;
        }

        return links;
    }

    private static ParsedDocumentLink? TryParseAt(
        string content,
        int start,
        NormalizedVirtualPath sourcePath)
    {
        var displayEnd = FindClosingBracket(content, start);
        if (displayEnd < 0
            || displayEnd + 1 >= content.Length
            || content[displayEnd + 1] != '(')
        {
            return null;
        }

        var displayText = content[(start + 1)..displayEnd];
        if (displayText.Length > 512
            || displayText.Contains('\r', StringComparison.Ordinal)
            || displayText.Contains('\n', StringComparison.Ordinal))
        {
            return null;
        }

        var openingParenthesis = displayEnd + 1;
        var cursor = openingParenthesis + 1;
        SkipSpaces(content, ref cursor);
        if (cursor >= content.Length)
        {
            return null;
        }

        int destinationStart;
        int destinationEnd;
        string destinationSource;
        if (content[cursor] == '<')
        {
            destinationStart = cursor;
            var close = FindUnescaped(content, '>', cursor + 1);
            if (close < 0)
            {
                return null;
            }
            destinationEnd = close + 1;
            destinationSource = content[(cursor + 1)..close];
            cursor = destinationEnd;
        }
        else
        {
            destinationStart = cursor;
            var depth = 0;
            while (cursor < content.Length)
            {
                var character = content[cursor];
                if (character == '\\' && cursor + 1 < content.Length)
                {
                    cursor += 2;
                    continue;
                }
                if (character is '\r' or '\n')
                {
                    return null;
                }
                if (character == '(')
                {
                    depth++;
                    cursor++;
                    continue;
                }
                if (character == ')')
                {
                    if (depth == 0)
                    {
                        break;
                    }
                    depth--;
                    cursor++;
                    continue;
                }
                if (depth == 0 && char.IsWhiteSpace(character))
                {
                    break;
                }
                cursor++;
            }
            if (depth != 0 || cursor == destinationStart)
            {
                return null;
            }
            destinationEnd = cursor;
            destinationSource = content[destinationStart..destinationEnd];
        }

        SkipSpaces(content, ref cursor);
        if (cursor >= content.Length)
        {
            return null;
        }
        if (content[cursor] == '"')
        {
            var titleEnd = FindUnescaped(content, '"', cursor + 1);
            if (titleEnd < 0)
            {
                return null;
            }
            cursor = titleEnd + 1;
            SkipSpaces(content, ref cursor);
        }
        if (cursor >= content.Length || content[cursor] != ')')
        {
            return null;
        }

        var rawDestination = UnescapeMarkdown(destinationSource);
        var resolved = MarkdownLinkPathResolver.Resolve(rawDestination, sourcePath);
        if (resolved is null)
        {
            return null;
        }

        return new ParsedDocumentLink(
            DocumentLinkKind.Markdown,
            resolved.RawTarget,
            null,
            resolved.CanonicalTargetKey,
            displayText,
            resolved.TargetHeading,
            resolved.TargetHeadingKey,
            start,
            cursor + 1,
            destinationStart,
            destinationEnd);
    }

    private static int FindClosingBracket(string content, int start)
    {
        var depth = 0;
        for (var index = start + 1; index < content.Length; index++)
        {
            if (content[index] == '\\' && index + 1 < content.Length)
            {
                index++;
                continue;
            }
            if (content[index] is '\r' or '\n')
            {
                return -1;
            }
            if (content[index] == '[')
            {
                depth++;
            }
            else if (content[index] == ']')
            {
                if (depth == 0)
                {
                    return index;
                }
                depth--;
            }
        }
        return -1;
    }

    private static int FindUnescaped(
        string content,
        char value,
        int start)
    {
        for (var index = start; index < content.Length; index++)
        {
            if (content[index] == '\\' && index + 1 < content.Length)
            {
                index++;
                continue;
            }
            if (content[index] == value)
            {
                return index;
            }
            if (content[index] is '\r' or '\n')
            {
                return -1;
            }
        }
        return -1;
    }

    private static string UnescapeMarkdown(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\'
                && index + 1 < value.Length
                && IsEscapable(value[index + 1]))
            {
                result.Append(value[++index]);
                continue;
            }
            result.Append(value[index]);
        }
        return result.ToString();
    }

    private static bool IsEscapable(char value) =>
        value is '\\' or '!' or '"' or '#' or '(' or ')' or '*' or '+'
            or ',' or '-' or '.' or '/' or ':' or ';' or '<' or '=' or '>'
            or '?' or '@' or '[' or ']' or '^' or '_' or '`' or '{' or '|'
            or '}' or '~';

    private static void SkipSpaces(string content, ref int cursor)
    {
        while (cursor < content.Length
               && content[cursor] is ' ' or '\t')
        {
            cursor++;
        }
    }

    private static bool IsImageMarker(string content, int bracketIndex)
    {
        if (bracketIndex == 0 || content[bracketIndex - 1] != '!')
        {
            return false;
        }
        return !WikiLinkParser.IsEscaped(content, bracketIndex - 1);
    }

}
