using System.Collections;
using System.Globalization;
using System.Text.Json;
using CipherVault.Core.Errors;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace CipherVault.Core.Documents;

internal static class FrontMatterDocument
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
        var closingStart = FindClosingDelimiter(content, bodyStart);
        if (closingStart < 0)
        {
            return false;
        }
        var closingEnd = FindLineEnd(content, closingStart);
        var end = NextLineStart(content, closingEnd);
        var yaml = content[bodyStart..closingStart];
        if (string.IsNullOrWhiteSpace(yaml))
        {
            snapshot = new FrontMatterSnapshot(
                bodyStart,
                closingStart,
                end,
                []);
            return true;
        }

        Dictionary<string, object?> values;
        YamlMappingNode root;
        try
        {
            values = new DeserializerBuilder()
                .WithDuplicateKeyChecking()
                .Build()
                .Deserialize<Dictionary<string, object?>>(yaml) ?? [];
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 ||
                stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                throw InvalidFrontMatter(
                    "Frontmatter must contain one top-level mapping.");
            }
            root = mapping;
        }
        catch (VaultException)
        {
            throw;
        }
        catch (YamlException exception)
        {
            throw InvalidFrontMatter(
                "Frontmatter YAML is invalid.",
                exception);
        }

        var fields = new List<FrontMatterField>();
        var entries = root.Children.ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            var pair = entries[index];
            if (pair.Key is not YamlScalarNode keyNode ||
                string.IsNullOrEmpty(keyNode.Value))
            {
                throw InvalidFrontMatter(
                    "Frontmatter keys must be non-empty scalars.");
            }
            var key = keyNode.Value;
            if (!values.TryGetValue(key, out var value))
            {
                throw InvalidFrontMatter(
                    $"Frontmatter value for {key} could not be resolved.");
            }
            var fieldStart = bodyStart + checked((int)keyNode.Start.Index);
            var nextFieldStart = index + 1 < entries.Length
                ? bodyStart + checked((int)entries[index + 1].Key.Start.Index)
                : closingStart;
            var fieldEnd = TrimTrailingLineBreaks(
                content,
                fieldStart,
                nextFieldStart);
            var jsonValue = ToJsonElement(value);
            fields.Add(new FrontMatterField(
                key,
                ValueTypeOf(jsonValue),
                jsonValue,
                fieldStart,
                fieldEnd));
        }

        snapshot = new FrontMatterSnapshot(
            bodyStart,
            closingStart,
            end,
            fields);
        return true;
    }

    private static int TrimTrailingLineBreaks(
        string content,
        int fieldStart,
        int boundary)
    {
        var end = Math.Clamp(boundary, fieldStart, content.Length);
        while (end > fieldStart &&
               content[end - 1] is '\r' or '\n')
        {
            end--;
        }
        return end;
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

    private static int FindClosingDelimiter(
        string content,
        int start)
    {
        var cursor = start;
        while (cursor < content.Length)
        {
            var lineEnd = FindLineEnd(content, cursor);
            if (IsDelimiter(content.AsSpan(cursor, lineEnd - cursor)))
            {
                return cursor;
            }
            cursor = NextLineStart(content, lineEnd);
        }
        return -1;
    }

    private static JsonElement ToJsonElement(object? value)
    {
        return JsonSerializer.SerializeToElement(ToPlainValue(value));
    }

    private static object? ToPlainValue(object? value)
    {
        if (value is null or string or bool ||
            value is byte or sbyte or short or ushort or int or uint or
                long or ulong or float or double or decimal)
        {
            return value;
        }
        if (value is DateTime dateTime)
        {
            return dateTime.ToString("O", CultureInfo.InvariantCulture);
        }
        if (value is DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
        }
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                result[Convert.ToString(
                    entry.Key,
                    CultureInfo.InvariantCulture) ?? string.Empty] =
                    ToPlainValue(entry.Value);
            }
            return result;
        }
        if (value is IEnumerable enumerable)
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
            {
                result.Add(ToPlainValue(item));
            }
            return result;
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string ValueTypeOf(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            JsonValueKind.Null => "null",
            _ => "unknown"
        };
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

    private static VaultException InvalidFrontMatter(
        string message,
        Exception? innerException = null)
    {
        return innerException is null
            ? new VaultException(
                VaultErrorCode.FrontMatterInvalid,
                message)
            : new VaultException(
                VaultErrorCode.FrontMatterInvalid,
                message,
                innerException);
    }
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
