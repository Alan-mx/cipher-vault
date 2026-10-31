namespace CipherVault.Core.Documents;

internal static class MarkdownFenceSyntax
{
    public static ReadOnlySpan<char> TrimBlockQuotePrefixes(
        ReadOnlySpan<char> line,
        out int depth)
    {
        depth = 0;
        while (!line.IsEmpty)
        {
            line = line.TrimStart();
            if (line.IsEmpty || line[0] != '>')
            {
                return line;
            }
            line = line[1..];
            depth++;
            if (!line.IsEmpty && line[0] is ' ' or '\t')
            {
                line = line[1..];
            }
        }
        return line;
    }

    public static bool TryReadOpening(
        ReadOnlySpan<char> line,
        out MarkdownFence fence)
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
        fence = new MarkdownFence(marker, length, 0);
        return true;
    }

    public static bool IsClosing(
        ReadOnlySpan<char> line,
        MarkdownFence opening)
    {
        if (!TryTrimBlockQuotePrefixes(
                line,
                opening.BlockQuoteDepth,
                out var content))
        {
            return false;
        }
        return TryReadOpening(content, out var closing) &&
            closing.Marker == opening.Marker &&
            closing.Length >= opening.Length &&
            content[closing.Length..].Trim().Length == 0;
    }

    private static bool TryTrimBlockQuotePrefixes(
        ReadOnlySpan<char> line,
        int count,
        out ReadOnlySpan<char> content)
    {
        content = line;
        for (var index = 0; index < count; index++)
        {
            content = content.TrimStart();
            if (content.IsEmpty || content[0] != '>')
            {
                content = line;
                return false;
            }
            content = content[1..];
            if (!content.IsEmpty && content[0] is ' ' or '\t')
            {
                content = content[1..];
            }
        }
        content = content.TrimStart();
        return true;
    }
}

internal readonly record struct MarkdownFence(
    char Marker,
    int Length,
    int BlockQuoteDepth);
