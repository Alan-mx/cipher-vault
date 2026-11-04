using System.Globalization;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Database;

namespace CipherVault.Core.Search;

internal static class SearchPropertySqlBuilder
{
    public static void AppendGroup(
        StringBuilder sql,
        VaultCommandLease command,
        IReadOnlyList<NormalizedSearchPropertyFilter> filters,
        SearchPropertyMatch match,
        ref int parameterIndex)
    {
        if (filters.Count == 0)
        {
            return;
        }
        if (!Enum.IsDefined(match))
        {
            throw new ArgumentOutOfRangeException(nameof(match));
        }

        sql.Append(" AND (");
        for (var index = 0; index < filters.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(match == SearchPropertyMatch.All ? " AND " : " OR ");
            }
            AppendPredicate(
                sql,
                command,
                filters[index],
                ref parameterIndex);
        }
        sql.Append(')');
    }

    private static void AppendPredicate(
        StringBuilder sql,
        VaultCommandLease command,
        NormalizedSearchPropertyFilter filter,
        ref int parameterIndex)
    {
        var pathName = $"$propertyPath{parameterIndex++}";
        command.Command.Parameters.AddWithValue(pathName, filter.JsonPath);
        sql.Append('(');
        switch (filter.Operator)
        {
            case SearchPropertyOperator.Exists:
                sql.Append("json_type(d.frontmatter_json, ")
                    .Append(pathName)
                    .Append(") IS ")
                    .Append(filter.Value.GetBoolean() ? "NOT NULL" : "NULL");
                break;
            case SearchPropertyOperator.Eq:
                AppendScalarEquality(
                    sql,
                    command,
                    pathName,
                    filter.Value,
                    filter.ValueKind,
                    ref parameterIndex);
                break;
            case SearchPropertyOperator.Contains:
                AppendContains(
                    sql,
                    command,
                    pathName,
                    filter.Value,
                    filter.ValueKind,
                    ref parameterIndex);
                break;
            case SearchPropertyOperator.In:
                AppendIn(
                    sql,
                    command,
                    pathName,
                    filter.Value,
                    ref parameterIndex);
                break;
            case SearchPropertyOperator.Gte:
            case SearchPropertyOperator.Lte:
                AppendRange(
                    sql,
                    command,
                    pathName,
                    filter.Value,
                    filter.ValueKind,
                    filter.Operator == SearchPropertyOperator.Gte
                        ? ">="
                        : "<=",
                    ref parameterIndex);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(filter));
        }
        sql.Append(')');
    }

    private static void AppendContains(
        StringBuilder sql,
        VaultCommandLease command,
        string pathName,
        JsonElement value,
        PropertyValueKind kind,
        ref int parameterIndex)
    {
        var valueName = $"$propertyValue{parameterIndex++}";
        AddScalarParameter(command, valueName, value, kind);
        var itemType = ItemTypeExpression(kind);
        sql.Append("(json_type(d.frontmatter_json, ")
            .Append(pathName)
            .Append(") = 'array' AND EXISTS (SELECT 1 FROM json_each(")
            .Append("d.frontmatter_json, ")
            .Append(pathName)
            .Append(") AS item WHERE ")
            .Append(itemType)
            .Append(" AND ")
            .Append(ItemValueExpression("item", valueName, kind))
            .Append("))");
        if (kind == PropertyValueKind.String)
        {
            sql.Append(" OR (json_type(d.frontmatter_json, ")
                .Append(pathName)
                .Append(") = 'text' AND ")
                .Append("cv_ordinal_ignore_case_contains(")
                .Append("json_extract(d.frontmatter_json, ")
                .Append(pathName)
                .Append("), ")
                .Append(valueName)
                .Append(") = 1)");
        }
    }

    private static void AppendIn(
        StringBuilder sql,
        VaultCommandLease command,
        string pathName,
        JsonElement values,
        ref int parameterIndex)
    {
        var added = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (added++ > 0)
            {
                sql.Append(" OR ");
            }
            var kind = KindOf(value);
            AppendScalarEquality(
                sql,
                command,
                pathName,
                value,
                kind,
                ref parameterIndex);
        }
    }

    private static void AppendRange(
        StringBuilder sql,
        VaultCommandLease command,
        string pathName,
        JsonElement value,
        PropertyValueKind kind,
        string comparison,
        ref int parameterIndex)
    {
        var valueName = $"$propertyValue{parameterIndex++}";
        AddScalarParameter(command, valueName, value, kind);
        if (kind == PropertyValueKind.Number)
        {
            sql.Append("json_type(d.frontmatter_json, ")
                .Append(pathName)
                .Append(") IN ('integer', 'real') AND ")
                .Append("json_extract(d.frontmatter_json, ")
                .Append(pathName)
                .Append(") ")
                .Append(comparison)
                .Append(' ')
                .Append(valueName);
        }
        else if (kind == PropertyValueKind.String)
        {
            sql.Append("json_type(d.frontmatter_json, ")
                .Append(pathName)
                .Append(") = 'text' AND ")
                .Append("json_extract(d.frontmatter_json, ")
                .Append(pathName)
                .Append(") COLLATE BINARY ")
                .Append(comparison)
                .Append(' ')
                .Append(valueName);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void AppendScalarEquality(
        StringBuilder sql,
        VaultCommandLease command,
        string pathName,
        JsonElement value,
        PropertyValueKind kind,
        ref int parameterIndex)
    {
        if (kind == PropertyValueKind.Null)
        {
            sql.Append("json_type(d.frontmatter_json, ")
                .Append(pathName)
                .Append(") = 'null' AND json_extract(d.frontmatter_json, ")
                .Append(pathName)
                .Append(") IS NULL");
            return;
        }

        var valueName = $"$propertyValue{parameterIndex++}";
        AddScalarParameter(command, valueName, value, kind);
        sql.Append("json_type(d.frontmatter_json, ")
            .Append(pathName)
            .Append(") ")
            .Append(JsonTypePredicate(kind))
            .Append(" AND json_extract(d.frontmatter_json, ")
            .Append(pathName)
            .Append(')');
        if (kind == PropertyValueKind.String)
        {
            sql.Append(" COLLATE BINARY");
        }
        sql.Append(" = ").Append(valueName);
    }

    private static string JsonTypePredicate(PropertyValueKind kind) =>
        kind switch
        {
            PropertyValueKind.Boolean => "IN ('true', 'false')",
            PropertyValueKind.Number => "IN ('integer', 'real')",
            PropertyValueKind.String => "= 'text'",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static string ItemTypeExpression(PropertyValueKind kind) =>
        kind switch
        {
            PropertyValueKind.Null => "item.type = 'null'",
            PropertyValueKind.Boolean => "item.type IN ('true', 'false')",
            PropertyValueKind.Number => "item.type IN ('integer', 'real')",
            PropertyValueKind.String => "item.type = 'text'",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static string ItemValueExpression(
        string itemAlias,
        string valueName,
        PropertyValueKind kind) =>
        kind == PropertyValueKind.Null
            ? $"{itemAlias}.value IS NULL"
            : kind == PropertyValueKind.String
                ? $"{itemAlias}.value COLLATE BINARY = {valueName}"
                : $"{itemAlias}.value = {valueName}";

    private static void AddScalarParameter(
        VaultCommandLease command,
        string name,
        JsonElement value,
        PropertyValueKind kind)
    {
        object parameter = kind switch
        {
            PropertyValueKind.Null => DBNull.Value,
            PropertyValueKind.Boolean => value.GetBoolean(),
            PropertyValueKind.String => value.GetString() ?? string.Empty,
            PropertyValueKind.Number => ParseNumberParameter(value.GetRawText()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        command.Command.Parameters.AddWithValue(name, parameter);
    }

    private static object ParseNumberParameter(string raw)
    {
        if (long.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var integer))
        {
            return integer;
        }
        return double.Parse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
    }

    private static PropertyValueKind KindOf(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => PropertyValueKind.Null,
            JsonValueKind.True or JsonValueKind.False => PropertyValueKind.Boolean,
            JsonValueKind.Number => PropertyValueKind.Number,
            JsonValueKind.String => PropertyValueKind.String,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
}
