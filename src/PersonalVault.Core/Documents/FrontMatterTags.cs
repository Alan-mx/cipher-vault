using System.Text.Json;

namespace PersonalVault.Core.Documents;

internal static class FrontMatterTags
{
    public static IReadOnlyList<string> Extract(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return [];
        }

        var end = normalized.IndexOf("\n---", 5, StringComparison.Ordinal);
        if (end < 0)
        {
            return [];
        }

        var lines = normalized[4..end].Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var inline = line[5..].Trim();
            if (inline.StartsWith('[') && inline.EndsWith(']'))
            {
                return inline[1..^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim().Trim('"', '\''))
                    .Where(value => value.Length > 0)
                    .ToList();
            }

            var tags = new List<string>();
            if (inline.Length > 0)
            {
                tags.AddRange(inline.Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
            for (var next = index + 1; next < lines.Length; next++)
            {
                var item = lines[next].Trim();
                if (!item.StartsWith("- ", StringComparison.Ordinal))
                {
                    break;
                }
                tags.Add(item[2..]);
            }
            return tags.Select(value => value.Trim().Trim('"', '\'')).ToList();
        }
        return [];
    }
}
