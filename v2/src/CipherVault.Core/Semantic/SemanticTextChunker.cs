using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Documents;

namespace CipherVault.Core.Semantic;

public sealed record SemanticChunkingRequest(
    Guid DocumentId,
    int SourceRevision,
    string Title,
    VaultDocumentFormat Format,
    string Content,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> ContextFields);

public sealed record SemanticChunk(
    int ChunkOrdinal,
    IReadOnlyList<string> HeadingPath,
    int StartOffset,
    int EndOffset,
    string Content,
    string ContentHash,
    string ProviderInput);

public static class SemanticTextChunker
{
    public const int TargetScalars = 1_000;
    public const int OverlapScalars = 150;

    public static IReadOnlyList<SemanticChunk> Chunk(
        SemanticChunkingRequest request,
        int maxInputTokens,
        Func<string, int> countInputTokens)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(countInputTokens);
        if (request.SourceRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        if (maxInputTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInputTokens));
        }

        var bodyStart = 0;
        if (request.Format == VaultDocumentFormat.Markdown
            && FrontMatterDocument.TryParse(request.Content, out var frontMatter))
        {
            bodyStart = frontMatter.End;
        }

        var sections = request.Format == VaultDocumentFormat.Markdown
            ? MarkdownSections(request.Content, bodyStart)
            : [new TextSection(bodyStart, request.Content.Length, [])];
        var prefix = BuildContextPrefix(request);
        if (countInputTokens(prefix) >= maxInputTokens)
        {
            throw new InvalidOperationException(
                "Semantic context prefix leaves no token budget for chunk text.");
        }
        var result = new List<SemanticChunk>();
        var ordinal = 0;
        foreach (var section in sections)
        {
            var (start, end) = TrimWhitespace(
                request.Content,
                section.Start,
                section.End);
            while (start < end)
            {
                var idealEnd = AdvanceScalars(
                    request.Content,
                    start,
                    end,
                    TargetScalars);
                var chunkEnd = FindPreferredBoundary(
                    request.Content,
                    start,
                    idealEnd,
                    end);
                if (chunkEnd <= start)
                {
                    chunkEnd = idealEnd;
                }
                var (chunkStart, trimmedEnd) = TrimWhitespace(
                    request.Content,
                    start,
                    chunkEnd);
                if (chunkStart >= trimmedEnd)
                {
                    start = chunkEnd;
                    continue;
                }
                chunkEnd = FitTokenLimit(
                    request.Content,
                    chunkStart,
                    trimmedEnd,
                    prefix,
                    maxInputTokens,
                    countInputTokens);
                if (chunkEnd <= chunkStart)
                {
                    throw new InvalidOperationException(
                        "Semantic chunk cannot fit the configured token limit.");
                }
                var content = request.Content[chunkStart..chunkEnd];
                var providerInput = prefix + content;
                result.Add(new SemanticChunk(
                    ordinal++,
                    section.HeadingPath,
                    chunkStart,
                    chunkEnd,
                    content,
                    SemanticProfileIdentity.Sha256Hex(content),
                    providerInput));
                if (chunkEnd >= end)
                {
                    break;
                }
                var scalarCount = CountScalars(
                    request.Content,
                    chunkStart,
                    chunkEnd);
                var overlap = Math.Min(
                    OverlapScalars,
                    Math.Max(1, scalarCount / 2));
                var next = AdvanceScalars(
                    request.Content,
                    chunkStart,
                    chunkEnd,
                    scalarCount - overlap);
                next = MovePastWhitespace(request.Content, next, chunkEnd);
                start = next > chunkStart ? next : chunkEnd;
            }
        }
        return result;
    }

    private static IReadOnlyList<TextSection> MarkdownSections(
        string content,
        int bodyStart)
    {
        var result = new List<TextSection>();
        var headingStack = new List<(int Level, string Text)>();
        var sectionStart = bodyStart;
        var currentPath = Array.Empty<string>();
        var inFence = false;
        var lineStart = bodyStart;
        while (lineStart < content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = content.Length;
            }
            var contentLineEnd = lineEnd > lineStart
                && content[lineEnd - 1] == '\r'
                ? lineEnd - 1
                : lineEnd;
            var line = content.AsSpan(
                lineStart,
                contentLineEnd - lineStart);
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
            }
            else if (!inFence
                && TryHeading(
                    line,
                    out var level,
                    out var headingText))
            {
                if (sectionStart < lineStart)
                {
                    result.Add(new TextSection(
                        sectionStart,
                        lineStart,
                        currentPath));
                }
                while (headingStack.Count > 0
                    && headingStack[^1].Level >= level)
                {
                    headingStack.RemoveAt(headingStack.Count - 1);
                }
                headingStack.Add((level, headingText));
                currentPath = headingStack
                    .Select(item => item.Text)
                    .ToArray();
                sectionStart = lineStart;
            }
            lineStart = lineEnd >= content.Length
                ? content.Length
                : lineEnd + 1;
        }
        if (sectionStart < content.Length)
        {
            result.Add(new TextSection(
                sectionStart,
                content.Length,
                currentPath));
        }
        return result;
    }

    private static bool TryHeading(
        ReadOnlySpan<char> line,
        out int level,
        out string text)
    {
        level = 0;
        text = string.Empty;
        while (level < line.Length
            && level < 6
            && line[level] == '#')
        {
            level++;
        }
        if (level == 0
            || level >= line.Length
            || line[level] is not (' ' or '\t'))
        {
            return false;
        }
        var value = line[level..].Trim().TrimEnd('#').Trim().ToString();
        if (value.Length == 0)
        {
            return false;
        }
        text = value;
        return true;
    }

    private static string BuildContextPrefix(
        SemanticChunkingRequest request)
    {
        var builder = new StringBuilder();
        Append("Title", request.Title);
        foreach (var key in new[] { "project", "type", "maturity" })
        {
            if (request.ContextFields.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                Append(key, value.Trim());
            }
        }
        if (request.Tags.Count > 0)
        {
            Append("Tags", string.Join(", ", request.Tags));
        }
        return builder.Length == 0 ? string.Empty : builder.Append('\n').ToString();

        void Append(string key, string value)
        {
            builder.Append(key).Append(": ").Append(value).Append('\n');
        }
    }

    private static int FitTokenLimit(
        string content,
        int start,
        int end,
        string prefix,
        int maxInputTokens,
        Func<string, int> countInputTokens)
    {
        if (countInputTokens(prefix + content[start..end]) <= maxInputTokens)
        {
            return end;
        }
        var low = start;
        var high = end;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            middle = AlignToRuneBoundary(content, middle, high);
            if (middle <= low)
            {
                break;
            }
            if (countInputTokens(prefix + content[start..middle])
                <= maxInputTokens)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private static int FindPreferredBoundary(
        string content,
        int start,
        int idealEnd,
        int sectionEnd)
    {
        if (idealEnd >= sectionEnd)
        {
            return sectionEnd;
        }
        var minimum = start + ((idealEnd - start) / 3);
        for (var index = idealEnd; index > minimum; index--)
        {
            if (content[index - 1] == '\n'
                && index >= 2
                && content[index - 2] == '\n')
            {
                return index - 1;
            }
            if (content[index - 1] == '\n'
                && index >= 3
                && content[index - 2] == '\r'
                && content[index - 3] == '\n')
            {
                return index - 1;
            }
        }
        for (var index = idealEnd; index > minimum; index--)
        {
            if (char.IsWhiteSpace(content[index - 1]))
            {
                return index;
            }
        }
        return idealEnd;
    }

    private static (int Start, int End) TrimWhitespace(
        string content,
        int start,
        int end)
    {
        while (start < end && char.IsWhiteSpace(content[start]))
        {
            start++;
        }
        while (end > start && char.IsWhiteSpace(content[end - 1]))
        {
            end--;
        }
        return (start, end);
    }

    private static int AdvanceScalars(
        string content,
        int start,
        int end,
        int count)
    {
        var offset = start;
        for (var index = 0; index < count && offset < end; index++)
        {
            offset += RuneAt(content, offset, end).Utf16SequenceLength;
        }
        return offset;
    }

    private static int CountScalars(
        string content,
        int start,
        int end)
    {
        var count = 0;
        var offset = start;
        while (offset < end)
        {
            offset += RuneAt(content, offset, end).Utf16SequenceLength;
            count++;
        }
        return count;
    }

    private static int MovePastWhitespace(
        string content,
        int start,
        int end)
    {
        var offset = start;
        while (offset < end && char.IsWhiteSpace(content[offset]))
        {
            offset++;
        }
        return offset;
    }

    private static int AlignToRuneBoundary(
        string content,
        int offset,
        int end)
    {
        if (offset > 0
            && offset < end
            && char.IsLowSurrogate(content[offset])
            && char.IsHighSurrogate(content[offset - 1]))
        {
            return offset - 1;
        }
        return offset;
    }

    private static Rune RuneAt(string content, int offset, int end)
    {
        var status = Rune.DecodeFromUtf16(
            content.AsSpan(offset, end - offset),
            out var rune,
            out var consumed);
        return status == System.Buffers.OperationStatus.Done
            ? rune
            : Rune.ReplacementChar;
    }

    private sealed record TextSection(
        int Start,
        int End,
        IReadOnlyList<string> HeadingPath);
}
