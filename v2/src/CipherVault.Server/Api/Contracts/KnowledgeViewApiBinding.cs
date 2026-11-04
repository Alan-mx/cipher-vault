using CipherVault.Core.Knowledge;

namespace CipherVault.Server.Api.Contracts;

public static class KnowledgeViewApiBinding
{
    public static KnowledgeViewField ParseField(string value) =>
        value switch
        {
            "project" => KnowledgeViewField.Project,
            "type" => KnowledgeViewField.Type,
            "maturity" => KnowledgeViewField.Maturity,
            _ => throw InvalidInput("Knowledge view field is invalid.")
        };

    public static bool ParseActiveOnly(
        HttpRequest request,
        KnowledgeViewField field,
        params string[] allowedKeys)
    {
        EnsureOnlyQueryKeys(request, allowedKeys);
        return ParseActiveOnlyValue(request, field);
    }

    public static bool ParseActiveOnlyValue(
        HttpRequest request,
        KnowledgeViewField field)
    {
        if (!request.Query.ContainsKey("activeOnly"))
        {
            return false;
        }
        if (field != KnowledgeViewField.Project)
        {
            throw InvalidInput(
                "activeOnly is only supported for project views.");
        }

        return ParseBoolean(
            SingleQueryValue(request, "activeOnly"),
            "activeOnly");
    }

    public static (string? FacetKey, bool Unset) ParseDocumentSelection(
        HttpRequest request)
    {
        var hasFacetKey = request.Query.ContainsKey("facetKey");
        var hasUnset = request.Query.ContainsKey("unset");
        if (hasFacetKey == hasUnset)
        {
            throw InvalidInput(
                "Exactly one of facetKey or unset=true is required.");
        }
        if (hasUnset)
        {
            var unset = SingleQueryValue(request, "unset");
            if (!string.Equals(unset, "true", StringComparison.Ordinal))
            {
                throw InvalidInput("unset must be true.");
            }
            return (null, true);
        }

        var facetKey = SingleQueryValue(request, "facetKey");
        if (string.IsNullOrWhiteSpace(facetKey))
        {
            throw InvalidInput("facetKey must not be empty.");
        }
        return (facetKey, false);
    }

    public static int? ParseOptionalPageSize(HttpRequest request)
    {
        if (!request.Query.ContainsKey("pageSize"))
        {
            return null;
        }
        var value = SingleQueryValue(request, "pageSize");
        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var pageSize))
        {
            throw InvalidInput("pageSize must be an integer.");
        }
        return pageSize;
    }

    public static string? ParseOptionalCursor(HttpRequest request)
    {
        if (!request.Query.ContainsKey("cursor"))
        {
            return null;
        }
        var values = request.Query["cursor"];
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            throw InvalidCursor();
        }
        return values[0]!;
    }

    public static void EnsureOnlyQueryKeys(
        HttpRequest request,
        params string[] allowedKeys)
    {
        var allowed = allowedKeys.ToHashSet(StringComparer.Ordinal);
        if (request.Query.Keys.Any(key => !allowed.Contains(key)))
        {
            throw InvalidInput("The knowledge view query is invalid.");
        }
    }

    public static bool ParseBoolean(string value, string parameterName) =>
        value switch
        {
            "true" => true,
            "false" => false,
            _ => throw InvalidInput(
                $"{parameterName} must be true or false.")
        };

    public static string SingleQueryValue(
        HttpRequest request,
        string parameterName)
    {
        var values = request.Query[parameterName];
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            throw InvalidInput(
                $"{parameterName} must contain exactly one value.");
        }
        return values[0]!;
    }

    public static ApiRequestException InvalidInput(string message) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            message);

    public static ApiRequestException InvalidCursor() =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidCursor,
            "Cursor is invalid for this knowledge view.");
}
