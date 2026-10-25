using System.IO;
using CipherVault.Core.Documents;

namespace CipherVault.Ui.Wpf;

internal static class LinkSuggestionEngine
{
    private const int MaxQueryLength = 120;
    private static readonly char[] UnsafeTargetCharacters = ['[', ']', '|', '#'];

    public static WikiLinkCompletion? FindCompletion(string text, int caretOffset)
    {
        if (caretOffset < 2 || caretOffset > text.Length)
        {
            return null;
        }
        var lineStart = text.LastIndexOf('\n', caretOffset - 1) + 1;
        var prefix = text.AsSpan(lineStart, caretOffset - lineStart);
        var relativeOpen = prefix.LastIndexOf("[[".AsSpan());
        if (relativeOpen < 0)
        {
            return null;
        }
        var openOffset = lineStart + relativeOpen;
        if (caretOffset + 1 < text.Length &&
            text[caretOffset] == ']' &&
            text[caretOffset + 1] == ']')
        {
            return null;
        }
        var closing = text.IndexOf(
            "]]",
            openOffset + 2,
            caretOffset - openOffset - 2,
            StringComparison.Ordinal);
        if (closing >= 0)
        {
            return null;
        }
        var query = text[(openOffset + 2)..caretOffset];
        if (query.Length > MaxQueryLength ||
            query.IndexOfAny(['\r', '\n', '|', '#', ']']) >= 0)
        {
            return null;
        }
        return new WikiLinkCompletion(openOffset, caretOffset, query);
    }

    public static IReadOnlyList<LinkSuggestion> Rank(
        IReadOnlyList<VaultCatalogItem> documents,
        Guid? currentDocumentId,
        string query,
        int limit = 20)
    {
        var normalizedQuery = query.Trim();
        var titleCounts = documents
            .Where(document => CanInsert(document))
            .GroupBy(
                document => document.Title.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);
        return documents
            .Where(document =>
                document.DocumentId != currentDocumentId &&
                CanInsert(document))
            .Select(document => new RankedSuggestion(
                document,
                Score(document, normalizedQuery),
                titleCounts[document.Title.Trim()]))
            .Where(item => item.Score < int.MaxValue)
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Document.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Document.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(item => new LinkSuggestion(
                item.Document.DocumentId,
                item.Document.Title,
                item.Document.VirtualPath,
                item.TitleCount > 1
                    ? RemoveExtension(item.Document.VirtualPath)
                    : item.Document.Title))
            .ToArray();
    }

    private static int Score(VaultCatalogItem document, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }
        if (document.Title.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (document.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (document.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (document.VirtualPath.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
        if (document.VirtualPath.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }
        if (document.VirtualPath.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }
        return int.MaxValue;
    }

    private static bool CanInsert(VaultCatalogItem document)
    {
        return document.Title.IndexOfAny(UnsafeTargetCharacters) < 0 &&
            document.VirtualPath.IndexOfAny(UnsafeTargetCharacters) < 0;
    }

    private static string RemoveExtension(string virtualPath)
    {
        var extension = Path.GetExtension(virtualPath);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                ? virtualPath[..^extension.Length]
                : virtualPath;
    }

    private sealed record RankedSuggestion(
        VaultCatalogItem Document,
        int Score,
        int TitleCount);
}

internal sealed record WikiLinkCompletion(
    int StartOffset,
    int CaretOffset,
    string Query);

internal sealed record LinkSuggestion(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    string InsertTarget);
