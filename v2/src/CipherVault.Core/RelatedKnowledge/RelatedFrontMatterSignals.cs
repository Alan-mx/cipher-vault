using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.RelatedKnowledge;

internal static class RelatedFrontMatterSignals
{
    internal const int MaxProperties = 8;
    internal const int MaxStringLength = 128;

    private static readonly HashSet<string> ReservedKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "id",
            "tags",
            "created",
            "updated",
            "project",
            "type"
        };

    internal static RelatedFrontMatterProfile Read(JsonElement frontMatter)
    {
        if (frontMatter.ValueKind != JsonValueKind.Object)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Document Frontmatter is not a JSON object.");
        }

        string? project = null;
        string? type = null;
        var properties = new List<RelatedScalarProperty>();
        foreach (var field in frontMatter.EnumerateObject())
        {
            if (field.Name.Equals("project", StringComparison.Ordinal))
            {
                project = ReadComparableString(field.Value);
                continue;
            }
            if (field.Name.Equals("type", StringComparison.Ordinal))
            {
                type = ReadComparableString(field.Value);
                continue;
            }
            if (ReservedKeys.Contains(field.Name))
            {
                continue;
            }

            var property = ReadScalar(field.Name, field.Value);
            if (property is not null)
            {
                properties.Add(property);
            }
        }

        return new RelatedFrontMatterProfile(
            project,
            type,
            properties
                .OrderBy(property => property.Key, StringComparer.Ordinal)
                .Take(MaxProperties)
                .ToArray());
    }

    internal static bool ValuesEqual(
        RelatedScalarProperty left,
        RelatedScalarProperty right) =>
        left.Key.Equals(right.Key, StringComparison.Ordinal)
        && left.Kind == right.Kind
        && left.Kind switch
        {
            RelatedScalarKind.String => string.Equals(
                left.StringValue,
                right.StringValue,
                StringComparison.OrdinalIgnoreCase),
            RelatedScalarKind.Number => left.NumberValue == right.NumberValue,
            RelatedScalarKind.Boolean => left.BooleanValue == right.BooleanValue,
            _ => false
        };

    private static RelatedScalarProperty? ReadScalar(
        string key,
        JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => ReadStringProperty(key, value),
            JsonValueKind.Number when value.TryGetDouble(out var number)
                && double.IsFinite(number) =>
                new RelatedScalarProperty(
                    key,
                    RelatedScalarKind.Number,
                    null,
                    number,
                    null),
            JsonValueKind.True or JsonValueKind.False =>
                new RelatedScalarProperty(
                    key,
                    RelatedScalarKind.Boolean,
                    null,
                    null,
                    value.GetBoolean()),
            _ => null
        };
    }

    private static RelatedScalarProperty? ReadStringProperty(
        string key,
        JsonElement value)
    {
        var text = Normalize(value.GetString());
        return text.Length is >= 1 and <= MaxStringLength
            ? new RelatedScalarProperty(
                key,
                RelatedScalarKind.String,
                text,
                null,
                null)
            : null;
    }

    private static string? ReadComparableString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = Normalize(value.GetString());
        return text.Length == 0 ? null : text;
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty)
            .Normalize(System.Text.NormalizationForm.FormC)
            .Trim();
}
