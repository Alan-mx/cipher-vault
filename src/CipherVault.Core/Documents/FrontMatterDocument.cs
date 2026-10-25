using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CipherVault.Core.Documents;

internal static partial class FrontMatterDocument
{
    public static bool TryParse(
        string content,
        out FrontMatterSnapshot snapshot)
    {
        snapshot = default!;
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        var openingEnd = FindLineEnd(content, 0);
        if (!IsDelimiter(content.AsSpan(0, openingEnd)))
        {
            return false;
        }
        var bodyStart = NextLineStart(content, openingEnd);
        var cursor = bodyStart;
        var fields = new List<FrontMatterField>();
        while (cursor < content.Length)
        {
            var lineEnd = FindLineEnd(content, cursor);
            var line = content.AsSpan(cursor, lineEnd - cursor);
            if (IsDelimiter(line))
            {
                snapshot = new FrontMatterSnapshot(
                    bodyStart,
                    cursor,
                    NextLineStart(content, lineEnd),
                    fields);
                return true;
            }
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
            {
                cursor = NextLineStart(content, lineEnd);
                continue;
            }

            var match = FieldKeyRegex().Match(line.ToString());
            if (!match.Success)
            {
                cursor = NextLineStart(content, lineEnd);
                continue;
            }

            var key = match.Groups[1].Value;
            var inlineValue = match.Groups[2].Value.Trim();
            var fieldStart = cursor;
            var fieldEnd = lineEnd;
            var valueText = inlineValue;
            if (inlineValue.Length == 0)
            {
                var next = NextLineStart(content, lineEnd);
                var items = new List<string>();
                while (next < content.Length)
                {
                    var itemEnd = FindLineEnd(content, next);
                    var itemLine = content.AsSpan(next, itemEnd - next);
                    if (itemLine.Length == 0)
                    {
                        break;
                    }
                    var trimmed = itemLine.TrimStart();
                    if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        break;
                    }
                    items.Add(trimmed[2..].Trim().ToString());
                    fieldEnd = itemEnd;
                    next = NextLineStart(content, itemEnd);
                }
                if (items.Count > 0)
                {
                    valueText = "[" + string.Join(", ", items) + "]";
                }
            }

            var (valueType, value) = ParseValue(valueText);
            fields.Add(new FrontMatterField(
                key,
                valueType,
                value,
                fieldStart,
                fieldEnd));
            cursor = NextLineStart(content, fieldEnd);
        }

        return false;
    }

    public static string RenderField(
        FrontMatterField field,
        JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray()
                .Select(item => "- " + RenderScalar(item))
                .ToArray();
            return items.Length == 0
                ? $"{field.Key}: []"
                : $"{field.Key}:\n{string.Join('\n', items)}";
        }
        return $"{field.Key}: {RenderScalar(value)}";
    }

    private static (string Type, JsonElement Value) ParseValue(string text)
    {
        if (text.Length == 0 ||
            string.Equals(text, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "~", StringComparison.Ordinal))
        {
            return ("null", JsonSerializer.SerializeToElement<string?>(null));
        }
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return ("boolean", JsonSerializer.SerializeToElement(true));
        }
        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            return ("boolean", JsonSerializer.SerializeToElement(false));
        }
        if (long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var integer))
        {
            return ("number", JsonSerializer.SerializeToElement(integer));
        }
        if (double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return ("number", JsonSerializer.SerializeToElement(number));
        }
        if (text.StartsWith('[') && text.EndsWith(']'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return ("array", document.RootElement.Clone());
                }
            }
            catch (JsonException)
            {
                var items = text[1..^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => Unquote(item.Trim()))
                    .ToArray();
                return ("array", JsonSerializer.SerializeToElement(items));
            }
        }
        return ("string", JsonSerializer.SerializeToElement(Unquote(text)));
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }

    private static string RenderScalar(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => RenderString(value.GetString() ?? string.Empty),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Null => "null",
            _ => JsonSerializer.Serialize(value.GetRawText())
        };
    }

    private static string RenderString(string value)
    {
        var requiresQuotes =
            value.Length == 0 ||
            value.Any(char.IsControl) ||
            value.IndexOfAny([':', '#', '[', ']', '{', '}', ',', '&', '*', '!', '|', '>', '@', '`']) >= 0 ||
            char.IsWhiteSpace(value[0]) ||
            char.IsWhiteSpace(value[^1]) ||
            value is "null" or "true" or "false" ||
            double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out _);
        return requiresQuotes
            ? JsonSerializer.Serialize(value)
            : value;
    }

    private static bool IsDelimiter(ReadOnlySpan<char> line)
    {
        return line.Trim().SequenceEqual("---");
    }

    private static int FindLineEnd(string content, int start)
    {
        var index = content.IndexOf('\n', start);
        if (index < 0)
        {
            return content.Length;
        }
        return index > start && content[index - 1] == '\r'
            ? index - 1
            : index;
    }

    private static int NextLineStart(string content, int lineEnd)
    {
        if (lineEnd >= content.Length)
        {
            return content.Length;
        }
        var start = content[lineEnd] == '\r' ? lineEnd + 2 : lineEnd + 1;
        return Math.Min(start, content.Length);
    }

    [GeneratedRegex(@"^([A-Za-z0-9_.-]+):[ \t]*(.*)$")]
    private static partial Regex FieldKeyRegex();
}

internal sealed record FrontMatterField(
    string Key,
    string ValueType,
    JsonElement Value,
    int StartOffset,
    int EndOffset);

internal sealed record FrontMatterSnapshot(
    int BodyStart,
    int ClosingStart,
    int End,
    IReadOnlyList<FrontMatterField> Fields);
