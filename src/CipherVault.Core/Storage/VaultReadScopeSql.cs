using System.Text;
using CipherVault.Core.Documents;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage;

internal static class VaultReadScopeSql
{
    public static void Append(
        StringBuilder sql,
        string pathColumn,
        VaultReadScope scope,
        string parameterPrefix)
    {
        if (!scope.IsRestricted)
        {
            return;
        }
        sql.Append(" AND (");
        for (var index = 0; index < scope.Folders.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(" OR ");
            }
            sql.Append(pathColumn)
                .Append(" = $")
                .Append(parameterPrefix)
                .Append(index)
                .Append(" OR ")
                .Append(pathColumn)
                .Append(" LIKE $")
                .Append(parameterPrefix)
                .Append(index)
                .Append("Prefix ESCAPE '\\'");
        }
        sql.Append(')');
    }

    public static void AddParameters(
        SqliteCommand command,
        VaultReadScope scope,
        string parameterPrefix)
    {
        for (var index = 0; index < scope.Folders.Count; index++)
        {
            command.Parameters.AddWithValue(
                $"${parameterPrefix}{index}",
                scope.Folders[index]);
            command.Parameters.AddWithValue(
                $"${parameterPrefix}{index}Prefix",
                EscapeLike(scope.Folders[index]) + "/%");
        }
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
