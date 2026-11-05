using System.Text.Json;

namespace CipherVault.Core.KnowledgeBase;

public sealed record SourceLocatorDraft(
    SourceLocatorType Type,
    string Key,
    JsonElement Value,
    string? Label,
    int StartOffset,
    int EndOffset,
    int Ordinal,
    int? ParentOrdinal);

public static class SourceLocatorFactory
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<SourceLocatorDraft> Build(
        string content,
        SourceContentFormat format)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (format == SourceContentFormat.Markdown)
        {
            return BuildMarkdown(normalized);
        }
        return BuildParagraphs(normalized, []);
    }

    private static IReadOnlyList<SourceLocatorDraft> BuildMarkdown(
        string content)
    {
        var lines = EnumerateLines(content).ToArray();
        var headings = new List<Heading>();
        var stack = new List<Heading>();
        foreach (var line in lines)
        {
            var text = line.Text.TrimStart();
            var level = 0;
            while (level < text.Length && level < 6 && text[level] == '#')
            {
                level++;
            }
            if (level == 0
                || level >= text.Length
                || !char.IsWhiteSpace(text[level]))
            {
                continue;
            }
            var title = text[level..].Trim().TrimEnd('#').Trim();
            if (title.Length == 0)
            {
                continue;
            }
            while (stack.Count > 0 && stack[^1].Level >= level)
            {
                stack.RemoveAt(stack.Count - 1);
            }
            var heading = new Heading(
                level,
                title,
                line.Start,
                stack.Select(item => item.Title).Append(title).ToArray());
            headings.Add(heading);
            stack.Add(heading);
        }

        var result = new List<SourceLocatorDraft>();
        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            var end = content.Length;
            for (var next = index + 1; next < headings.Count; next++)
            {
                if (headings[next].Level <= heading.Level)
                {
                    end = headings[next].Start;
                    break;
                }
            }
            var value = JsonSerializer.SerializeToElement(
                heading.Path,
                JsonOptions);
            result.Add(new SourceLocatorDraft(
                SourceLocatorType.Section,
                value.GetRawText(),
                value,
                string.Join(" / ", heading.Path),
                heading.Start,
                end,
                result.Count,
                null));
        }

        var paragraphIndex = 0;
        foreach (var block in ParagraphBlocks(content))
        {
            var path = headings
                .LastOrDefault(heading => heading.Start <= block.Start)
                ?.Path ?? [];
            var paragraphOrdinal = paragraphIndex++;
            var value = JsonSerializer.SerializeToElement(
                new
                {
                    sectionPath = path,
                    ordinal = paragraphOrdinal
                },
                JsonOptions);
            result.Add(new SourceLocatorDraft(
                SourceLocatorType.Paragraph,
                value.GetRawText(),
                value,
                path.Count == 0
                    ? $"段落 {paragraphOrdinal + 1}"
                    : $"{string.Join(" / ", path)} / 段落 {paragraphOrdinal + 1}",
                block.Start,
                block.End,
                result.Count,
                null));
        }
        return result;
    }

    private static IReadOnlyList<SourceLocatorDraft> BuildParagraphs(
        string content,
        IReadOnlyList<string> path)
    {
        var result = new List<SourceLocatorDraft>();
        var ordinal = 0;
        foreach (var block in ParagraphBlocks(content))
        {
            var value = JsonSerializer.SerializeToElement(
                new
                {
                    sectionPath = path,
                    ordinal
                },
                JsonOptions);
            result.Add(new SourceLocatorDraft(
                SourceLocatorType.Paragraph,
                value.GetRawText(),
                value,
                $"段落 {ordinal + 1}",
                block.Start,
                block.End,
                ordinal,
                null));
            ordinal++;
        }
        return result;
    }

    private static IEnumerable<TextBlock> ParagraphBlocks(string content)
    {
        var lineStart = 0;
        var blockStart = -1;
        var blockEnd = -1;
        while (lineStart <= content.Length)
        {
            var newline = content.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? content.Length : newline;
            var line = content[lineStart..lineEnd];
            if (string.IsNullOrWhiteSpace(line)
                || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                if (blockStart >= 0)
                {
                    yield return new TextBlock(blockStart, blockEnd);
                    blockStart = -1;
                }
            }
            else
            {
                if (blockStart < 0)
                {
                    blockStart = lineStart;
                }
                blockEnd = lineEnd;
            }
            if (newline < 0)
            {
                break;
            }
            lineStart = newline + 1;
        }
        if (blockStart >= 0)
        {
            yield return new TextBlock(blockStart, blockEnd);
        }
    }

    private static IEnumerable<TextLine> EnumerateLines(string content)
    {
        var start = 0;
        while (start <= content.Length)
        {
            var newline = content.IndexOf('\n', start);
            var end = newline < 0 ? content.Length : newline;
            yield return new TextLine(start, content[start..end]);
            if (newline < 0)
            {
                yield break;
            }
            start = newline + 1;
        }
    }

    private sealed record TextBlock(int Start, int End);
    private sealed record TextLine(int Start, string Text);
    private sealed record Heading(
        int Level,
        string Title,
        int Start,
        IReadOnlyList<string> Path);
}
