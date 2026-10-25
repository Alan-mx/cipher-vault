using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Documents;

internal static class DocumentPatchEngine
{
    private const int MaxDiffBytes = 64 * 1024;

    public static PatchComputation Apply(
        string currentContent,
        VaultNotePatchCommand command)
    {
        ValidateCommand(command);
        var updated = command.TargetKind switch
        {
            "heading" => PatchHeading(currentContent, command),
            "frontmatter" => PatchFrontMatter(currentContent, command),
            _ => throw InvalidCommand("The patch target kind is invalid.")
        };
        var diff = BuildUnifiedDiff(currentContent, updated);
        return new PatchComputation(
            updated,
            !string.Equals(currentContent, updated, StringComparison.Ordinal),
            diff.Text,
            diff.Truncated);
    }

    private static string PatchHeading(
        string content,
        VaultNotePatchCommand command)
    {
        var path = command.HeadingPath?
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToArray() ?? [];
        if (path.Length == 0)
        {
            throw InvalidCommand("A heading path is required.");
        }
        var headings = FindHeadings(content);
        var matches = headings
            .Where(heading => heading.Path.SequenceEqual(
                path,
                StringComparer.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new VaultException(
                VaultErrorCode.PatchTargetNotFound,
                matches.Length == 0
                    ? "The target heading was not found."
                    : "The heading path is ambiguous.");
        }

        var target = matches[0];
        if (string.Equals(command.Operation, "delete", StringComparison.Ordinal))
        {
            return content.Remove(
                target.SectionStart,
                target.BodyEnd - target.SectionStart);
        }

        var markdown = command.ContentMarkdown ?? string.Empty;
        if (markdown.Length == 0)
        {
            throw InvalidCommand("Heading patch content is required.");
        }
        var body = content[target.BodyStart..target.BodyEnd];
        var replacement = command.Operation switch
        {
            "append" => AppendBlock(body, markdown),
            "prepend" => PrependBlock(body, markdown),
            "replace" => NormalizeBlock(markdown),
            _ => throw InvalidCommand("The heading patch operation is invalid.")
        };
        return string.Concat(
            content.AsSpan(0, target.BodyStart),
            replacement,
            content.AsSpan(target.BodyEnd));
    }

    private static string PatchFrontMatter(
        string content,
        VaultNotePatchCommand command)
    {
        var key = command.FrontMatterKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw InvalidCommand("A frontmatter key is required.");
        }
        if (!FrontMatterDocument.TryParse(content, out var snapshot))
        {
            throw new VaultException(
                VaultErrorCode.FrontMatterInvalid,
                "The document does not contain valid frontmatter.");
        }
        var matchingFields = snapshot.Fields
            .Where(candidate => string.Equals(
                candidate.Key,
                key,
                StringComparison.Ordinal))
            .ToArray();
        if (matchingFields.Length > 1)
        {
            throw new VaultException(
                VaultErrorCode.FrontMatterInvalid,
                "The frontmatter key is duplicated.");
        }
        if (matchingFields.Length == 0)
        {
            throw new VaultException(
                VaultErrorCode.PatchTargetNotFound,
                "The frontmatter field was not found.");
        }
        var field = matchingFields[0];

        var removeEnd = field.EndOffset;
        if (removeEnd < content.Length && content[removeEnd] == '\r')
        {
            removeEnd += 2;
        }
        else if (removeEnd < content.Length && content[removeEnd] == '\n')
        {
            removeEnd++;
        }

        if (string.Equals(command.Operation, "delete", StringComparison.Ordinal))
        {
            return content.Remove(field.StartOffset, removeEnd - field.StartOffset);
        }

        var value = command.Value
            ?? throw new VaultException(
                VaultErrorCode.FrontMatterInvalid,
                "A frontmatter value is required.");
        if (field.ValueType == "array" &&
            value.ValueKind == JsonValueKind.Array)
        {
            EnsureScalarArray(value);
        }
        var replacementValue = command.Operation switch
        {
            "replace" => ReplaceFrontMatterValue(field, value),
            "append" => MergeArrayValue(field, value, prepend: false),
            "prepend" => MergeArrayValue(field, value, prepend: true),
            _ => throw InvalidCommand("The frontmatter patch operation is invalid.")
        };
        var rendered = FrontMatterDocument.RenderField(field, replacementValue);
        return string.Concat(
            content.AsSpan(0, field.StartOffset),
            rendered,
            content.AsSpan(field.EndOffset));
    }

    private static JsonElement ReplaceFrontMatterValue(
        FrontMatterField field,
        JsonElement value)
    {
        var valid = field.ValueType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => IsScalarArray(value),
            "null" => value.ValueKind is
                JsonValueKind.String or
                JsonValueKind.Number or
                JsonValueKind.True or
                JsonValueKind.False,
            _ => false
        };
        if (!valid)
        {
            throw new VaultException(
                VaultErrorCode.FrontMatterInvalid,
                "The frontmatter value type does not match the existing field.");
        }
        return value.Clone();
    }

    private static JsonElement MergeArrayValue(
        FrontMatterField field,
        JsonElement value,
        bool prepend)
    {
        if (field.ValueType != "array" ||
            field.Value.ValueKind != JsonValueKind.Array)
        {
            throw new VaultException(
                VaultErrorCode.FrontMatterInvalid,
                "Append and prepend are supported only for frontmatter arrays.");
        }
        var additions = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.Clone()).ToArray()
            : [value.Clone()];
        var existing = field.Value.EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
        var merged = prepend
            ? additions.Concat(existing).ToArray()
            : existing.Concat(additions).ToArray();
        var mergedValue = JsonSerializer.SerializeToElement(merged);
        EnsureScalarArray(mergedValue);
        return mergedValue;
    }

    private static bool IsScalarArray(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Array &&
            value.EnumerateArray().All(item =>
                item.ValueKind is not (
                    JsonValueKind.Object or
                    JsonValueKind.Array or
                    JsonValueKind.Undefined));
    }

    private static void EnsureScalarArray(JsonElement value)
    {
        if (!IsScalarArray(value))
        {
            throw new VaultException(
                VaultErrorCode.FrontMatterInvalid,
                "Frontmatter arrays support only scalar values.");
        }
    }

    private static void ValidateCommand(VaultNotePatchCommand command)
    {
        if (command.TargetKind is not ("heading" or "frontmatter") ||
            command.Operation is not ("append" or "prepend" or "replace" or "delete"))
        {
            throw InvalidCommand("The patch command is invalid.");
        }
        if (command.TargetKind == "heading" &&
            (command.HeadingPath is null || command.HeadingPath.Count == 0))
        {
            throw InvalidCommand("A heading path is required.");
        }
        if (command.TargetKind == "frontmatter" &&
            string.IsNullOrWhiteSpace(command.FrontMatterKey))
        {
            throw InvalidCommand("A frontmatter key is required.");
        }
        if (command.Operation == "delete")
        {
            return;
        }
        if (command.TargetKind == "heading" &&
            string.IsNullOrEmpty(command.ContentMarkdown))
        {
            throw InvalidCommand("Heading patch content is required.");
        }
        if (command.TargetKind == "frontmatter" &&
            command.Value is null)
        {
            throw new VaultException(
                VaultErrorCode.FrontMatterInvalid,
                "A frontmatter value is required.");
        }
    }

    private static string AppendBlock(string body, string markdown)
    {
        var existing = body.TrimEnd();
        var addition = markdown.Trim();
        return existing.Length == 0
            ? NormalizeBlock(addition)
            : existing + "\n\n" + NormalizeBlock(addition);
    }

    private static string PrependBlock(string body, string markdown)
    {
        var addition = markdown.Trim();
        var existing = body.Trim('\r', '\n');
        return existing.Length == 0
            ? NormalizeBlock(addition)
            : NormalizeBlock(addition) + existing;
    }

    private static string NormalizeBlock(string value)
    {
        return value.TrimEnd() + "\n\n";
    }

    private static IReadOnlyList<HeadingLocation> FindHeadings(string content)
    {
        var headings = new List<HeadingLocation>();
        var open = new List<HeadingLocation>();
        MarkdownFence? openingFence = null;
        var lineStart = 0;
        while (lineStart < content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            lineEnd = lineEnd < 0 ? content.Length : lineEnd;
            var line = content.AsSpan(lineStart, lineEnd - lineStart)
                .TrimEnd('\r');
            var trimmed = line.TrimStart();
            var fenceLine =
                MarkdownFenceSyntax.TrimBlockQuotePrefixes(
                    trimmed,
                    out var blockQuoteDepth);
            if (openingFence is { } opening)
            {
                if (MarkdownFenceSyntax.IsClosing(trimmed, opening))
                {
                    openingFence = null;
                }
            }
            else if (!MarkdownFenceSyntax.TryReadOpening(
                         fenceLine,
                         out var fence))
            {
                var level = 0;
                while (level < line.Length && line[level] == '#')
                {
                    level++;
                }
                if (level is >= 1 and <= 6 &&
                    level < line.Length &&
                    (line[level] == ' ' || line[level] == '\t'))
                {
                    var text = line[(level + 1)..]
                        .Trim()
                        .TrimEnd('#')
                        .Trim()
                        .ToString();
                    if (text.Length > 0)
                    {
                        while (open.Count > 0 && open[^1].Level >= level)
                        {
                            var closed = open[^1];
                            closed.BodyEnd = lineStart;
                            open.RemoveAt(open.Count - 1);
                        }
                        var path = open
                            .Select(heading => heading.Text)
                            .Append(text)
                            .ToArray();
                        var bodyStart = lineEnd < content.Length ? lineEnd + 1 : content.Length;
                        var heading = new HeadingLocation(
                            level,
                            text,
                            path,
                            lineStart,
                            bodyStart);
                        headings.Add(heading);
                        open.Add(heading);
                    }
                }
            }
            else
            {
                openingFence = fence with
                {
                    BlockQuoteDepth = blockQuoteDepth
                };
            }
            if (lineEnd >= content.Length)
            {
                break;
            }
            lineStart = lineEnd + 1;
        }
        foreach (var heading in open)
        {
            heading.BodyEnd = content.Length;
        }
        return headings;
    }

    private static (string Text, bool Truncated) BuildUnifiedDiff(
        string before,
        string after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return (string.Empty, false);
        }
        var oldLines = NormalizeLines(before);
        var newLines = NormalizeLines(after);
        var prefix = 0;
        while (prefix < oldLines.Length &&
               prefix < newLines.Length &&
               string.Equals(
                   oldLines[prefix],
                   newLines[prefix],
                   StringComparison.Ordinal))
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < oldLines.Length - prefix &&
               suffix < newLines.Length - prefix &&
               string.Equals(
                   oldLines[^(suffix + 1)],
                   newLines[^(suffix + 1)],
                   StringComparison.Ordinal))
        {
            suffix++;
        }

        var builder = new StringBuilder();
        builder.Append("@@ -")
            .Append(prefix + 1)
            .Append(',')
            .Append(oldLines.Length - prefix - suffix)
            .Append(" +")
            .Append(prefix + 1)
            .Append(',')
            .Append(newLines.Length - prefix - suffix)
            .AppendLine(" @@");
        for (var index = prefix; index < oldLines.Length - suffix; index++)
        {
            builder.Append('-').AppendLine(oldLines[index]);
        }
        for (var index = prefix; index < newLines.Length - suffix; index++)
        {
            builder.Append('+').AppendLine(newLines[index]);
        }
        var text = builder.ToString();
        if (Encoding.UTF8.GetByteCount(text) <= MaxDiffBytes)
        {
            return (text, false);
        }
        return (TruncateUtf8(text, MaxDiffBytes), true);
    }

    private static string[] NormalizeLines(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n')
            .Split('\n');
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = Math.Min(bytes.Length, maxBytes);
        while (length > 0 && (bytes[length - 1] & 0xC0) == 0x80)
        {
            length--;
        }
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static VaultException InvalidCommand(string message)
    {
        return new VaultException(VaultErrorCode.InvalidInput, message);
    }

    private sealed class HeadingLocation(
        int level,
        string text,
        string[] path,
        int sectionStart,
        int bodyStart)
    {
        public int Level { get; } = level;
        public string Text { get; } = text;
        public string[] Path { get; } = path;
        public int SectionStart { get; } = sectionStart;
        public int BodyStart { get; } = bodyStart;
        public int BodyEnd { get; set; }
    }
}

internal sealed record PatchComputation(
    string Content,
    bool Changed,
    string UnifiedDiff,
    bool DiffTruncated);
