using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CipherVault.Core.Documents;

internal static partial class MarkdownHeadings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Extract(string content)
    {
        var headings = new List<MarkdownHeading>();
        var inFence = false;
        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                continue;
            }

            var match = HeadingRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }
            var text = match.Groups[2].Value.Trim().TrimEnd('#').Trim();
            if (text.Length > 0)
            {
                headings.Add(new MarkdownHeading(match.Groups[1].Value.Length, text));
            }
        }
        return JsonSerializer.Serialize(headings, JsonOptions);
    }

    public static IReadOnlyList<string> Parse(string json)
    {
        var headings = JsonSerializer.Deserialize<List<MarkdownHeading>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        return headings.Select(heading => $"H{heading.Level}: {heading.Text}").ToList();
    }

    [GeneratedRegex(@"^(#{1,6})[ \t]+(.+?)[ \t]*$")]
    private static partial Regex HeadingRegex();

    internal sealed record MarkdownHeading(int Level, string Text);
}
