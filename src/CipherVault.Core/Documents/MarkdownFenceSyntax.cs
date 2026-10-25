namespace CipherVault.Core.Documents;

internal static class MarkdownFenceSyntax
{
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
        fence = new MarkdownFence(marker, length);
        return true;
    }

    public static bool IsClosing(
        ReadOnlySpan<char> line,
        MarkdownFence opening)
    {
        return TryReadOpening(line, out var closing) &&
            closing.Marker == opening.Marker &&
            closing.Length >= opening.Length &&
            line[closing.Length..].Trim().Length == 0;
    }
}

internal readonly record struct MarkdownFence(char Marker, int Length);
