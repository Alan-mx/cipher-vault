using System.Globalization;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Search;

internal enum PropertyValueKind
{
    Null,
    Boolean,
    Number,
    String,
    ScalarArray
}

internal sealed record NormalizedSearchPropertyFilter(
    string Field,
    string JsonPath,
    SearchPropertyOperator Operator,
    PropertyValueKind ValueKind,
    JsonElement Value,
    string CanonicalKey);

internal static class SearchPropertyFilterNormalizer
{
    internal const int MaxFilters = 16;
    internal const int MaxFieldLength = 128;
    internal const int MaxPathSegments = 4;
    internal const int MaxStringLength = 512;
    internal const int MaxInValues = 32;

    public static IReadOnlyList<NormalizedSearchPropertyFilter> Normalize(
        IReadOnlyList<SearchPropertyFilter>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return [];
        }
        if (filters.Count > MaxFilters)
        {
            throw InvalidInput(
                $"At most {MaxFilters} property filters are allowed.");
        }

        var normalized = filters
            .Select(NormalizeOne)
            .OrderBy(item => item.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
        return normalized;
    }

    public static string BuildFingerprintKey(
        IReadOnlyList<NormalizedSearchPropertyFilter> filters,
        SearchPropertyMatch match)
    {
        if (filters.Count == 0)
        {
            return string.Empty;
        }
        if (!Enum.IsDefined(match))
        {
            throw InvalidInput("propertyMatch is invalid.");
        }
        return string.Join(
            '\u001F',
            filters
                .Select(item => item.CanonicalKey)
                .Order(StringComparer.Ordinal))
            + '\u001E'
            + match.ToString().ToLowerInvariant();
    }

    private static NormalizedSearchPropertyFilter NormalizeOne(
        SearchPropertyFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var (field, jsonPath) = NormalizeField(filter.Field);
        if (!Enum.IsDefined(filter.Operator))
        {
            throw InvalidInput("Property operator is invalid.");
        }

        var (kind, value, canonical) = NormalizeValue(
            filter.Operator,
            filter.Value);
        return new NormalizedSearchPropertyFilter(
            field,
            jsonPath,
            filter.Operator,
            kind,
            value,
            $"{field}\u001F{OperatorName(filter.Operator)}\u001F{canonical}");
    }

    private static (string Field, string JsonPath) NormalizeField(
        string? field)
    {
        if (string.IsNullOrEmpty(field)
            || field.Length > MaxFieldLength)
        {
            throw InvalidInput(
                $"Property field must contain 1 to {MaxFieldLength} characters.");
        }
        var segments = field.Split('.');
        if (segments.Length is < 1 or > MaxPathSegments)
        {
            throw InvalidInput(
                $"Property field supports at most {MaxPathSegments} segments.");
        }
        foreach (var segment in segments)
        {
            if (segment.Length == 0
                || !segment.All(character =>
                    character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '_'
                    or '-'))
            {
                throw InvalidInput("Property field contains an invalid segment.");
            }
        }
        if (string.Equals(segments[0], "tags", StringComparison.Ordinal))
        {
            throw InvalidInput(
                "The top-level tags field must use the tags filter.");
        }

        return (
            field,
            "$." + string.Join(
                '.',
                segments.Select(segment => $"\"{segment}\"")));
    }

    private static (
        PropertyValueKind Kind,
        JsonElement Value,
        string Canonical) NormalizeValue(
            SearchPropertyOperator operation,
            JsonElement value)
    {
        return operation switch
        {
            SearchPropertyOperator.Exists => NormalizeExists(value),
            SearchPropertyOperator.Eq => NormalizeScalar(value, "eq"),
            SearchPropertyOperator.Contains => NormalizeScalar(value, "contains"),
            SearchPropertyOperator.In => NormalizeIn(value),
            SearchPropertyOperator.Gte => NormalizeComparable(value, "gte"),
            SearchPropertyOperator.Lte => NormalizeComparable(value, "lte"),
            _ => throw InvalidInput("Property operator is invalid.")
        };
    }

    private static (
        PropertyValueKind Kind,
        JsonElement Value,
        string Canonical) NormalizeExists(JsonElement value)
    {
        if (value.ValueKind is not (
            JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidInput("exists requires a boolean value.");
        }
        var boolean = value.GetBoolean();
        return (
            PropertyValueKind.Boolean,
            value.Clone(),
            boolean ? "b:true" : "b:false");
    }

    private static (
        PropertyValueKind Kind,
        JsonElement Value,
        string Canonical) NormalizeScalar(
            JsonElement value,
            string operation)
    {
        var (kind, canonical) = ScalarKindAndCanonical(value, operation);
        return (kind, value.Clone(), canonical);
    }

    private static (
        PropertyValueKind Kind,
        JsonElement Value,
        string Canonical) NormalizeComparable(
            JsonElement value,
            string operation)
    {
        if (value.ValueKind is not (
            JsonValueKind.Number or JsonValueKind.String))
        {
            throw InvalidInput($"{operation} requires a number or string value.");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            ValidateString(value.GetString() ?? string.Empty);
        }
        var (kind, canonical) = ScalarKindAndCanonical(value, operation);
        return (kind, value.Clone(), canonical);
    }

    private static (
        PropertyValueKind Kind,
        JsonElement Value,
        string Canonical) NormalizeIn(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidInput("in requires an array of scalar values.");
        }
        var values = new List<CanonicalScalar>();
        foreach (var item in value.EnumerateArray())
        {
            var (kind, canonical) = ScalarKindAndCanonical(item, "in");
            values.Add(new CanonicalScalar(kind, item.Clone(), canonical));
        }
        if (values.Count is < 1 or > MaxInValues)
        {
            throw InvalidInput(
                $"in requires between 1 and {MaxInValues} values.");
        }
        var normalized = values
            .GroupBy(item => item.Canonical, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Canonical, StringComparer.Ordinal)
            .ToArray();
        return (
            PropertyValueKind.ScalarArray,
            JsonSerializer.SerializeToElement(
                normalized.Select(item => item.Value)),
            string.Join('\u001F', normalized.Select(item => item.Canonical)));
    }

    private static (PropertyValueKind Kind, string Canonical)
        ScalarKindAndCanonical(
            JsonElement value,
            string operation)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => (PropertyValueKind.Null, "null:"),
            JsonValueKind.True => (PropertyValueKind.Boolean, "b:true"),
            JsonValueKind.False => (PropertyValueKind.Boolean, "b:false"),
            JsonValueKind.Number => (
                PropertyValueKind.Number,
                "n:" + CanonicalNumber(value.GetRawText())),
            JsonValueKind.String => (
                PropertyValueKind.String,
                "s:" + ValidateString(value.GetString() ?? string.Empty)),
            _ => throw InvalidInput(
                $"{operation} requires a JSON scalar value.")
        };
    }

    private static string ValidateString(string value)
    {
        int scalarCount;
        try
        {
            scalarCount = UnicodeText.CountScalars(value);
        }
        catch (ArgumentException)
        {
            throw InvalidInput("Property string contains invalid Unicode data.");
        }
        if (scalarCount > MaxStringLength)
        {
            throw InvalidInput(
                $"Property string values must not exceed {MaxStringLength} characters.");
        }
        return value;
    }

    private static string CanonicalNumber(string raw)
    {
        if (decimal.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var number))
        {
            return number == 0 ? "0" : number.ToString(
                "G29",
                CultureInfo.InvariantCulture);
        }
        return raw;
    }

    private static string OperatorName(SearchPropertyOperator operation) =>
        operation switch
        {
            SearchPropertyOperator.Exists => "exists",
            SearchPropertyOperator.Eq => "eq",
            SearchPropertyOperator.Contains => "contains",
            SearchPropertyOperator.In => "in",
            SearchPropertyOperator.Gte => "gte",
            SearchPropertyOperator.Lte => "lte",
            _ => throw InvalidInput("Property operator is invalid.")
        };

    private static VaultException InvalidInput(string message) =>
        new(VaultErrorCode.InvalidInput, message);

    private sealed record CanonicalScalar(
        PropertyValueKind Kind,
        JsonElement Value,
        string Canonical);
}
