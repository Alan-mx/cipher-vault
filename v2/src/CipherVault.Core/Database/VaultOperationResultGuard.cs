using System.Collections;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Reflection;
using CipherVault.Core.Crypto;
using CipherVault.Core.Errors;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Database;

internal static class VaultOperationResultGuard
{
    private const int MaximumDepth = 32;

    internal static void ValidateResult(object? result)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        ValidateValue(result, visited, depth: 0);
    }

    private static void ValidateValue(
        object? value,
        HashSet<object> visited,
        int depth)
    {
        if (value is null)
        {
            return;
        }

        if (depth > MaximumDepth)
        {
            throw Rejected();
        }

        var type = value.GetType();
        if (IsAllowedScalar(type))
        {
            return;
        }

        if (IsForbidden(value, type))
        {
            throw Rejected();
        }

        if (!type.IsValueType && !visited.Add(value))
        {
            throw Rejected();
        }

        if (value is Array array)
        {
            foreach (var item in array)
            {
                ValidateValue(item, visited, depth + 1);
            }

            return;
        }

        var genericType = type.IsGenericType ? type.GetGenericTypeDefinition() : null;
        if (type == typeof(string)
            || genericType == typeof(List<>)
            || genericType == typeof(ReadOnlyCollection<>)
            || genericType == typeof(ImmutableArray<>)
            || genericType == typeof(ImmutableList<>))
        {
            foreach (var item in (IEnumerable)value)
            {
                ValidateValue(item, visited, depth + 1);
            }

            return;
        }

        if (genericType == typeof(Dictionary<,>)
            || genericType == typeof(ImmutableDictionary<,>))
        {
            foreach (DictionaryEntry entry in (IDictionary)value)
            {
                ValidateValue(entry.Key, visited, depth + 1);
                ValidateValue(entry.Value, visited, depth + 1);
            }

            return;
        }

        if (value is IEnumerable
            || ImplementsOpenGeneric(type, typeof(IAsyncEnumerable<>))
            || value is IQueryable)
        {
            throw Rejected();
        }

        ValidateObjectGraph(value, type, visited, depth);
    }

    private static void ValidateObjectGraph(
        object value,
        Type type,
        HashSet<object> visited,
        int depth)
    {
        try
        {
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0
                    || property.GetMethod is not { IsStatic: false })
                {
                    continue;
                }

                ValidateValue(property.GetValue(value), visited, depth + 1);
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                ValidateValue(field.GetValue(value), visited, depth + 1);
            }
        }
        catch (VaultException)
        {
            throw;
        }
        catch
        {
            throw Rejected();
        }
    }

    private static bool IsAllowedScalar(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(string)
        || type == typeof(decimal)
        || type == typeof(Guid)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(TimeSpan)
        || type == typeof(CancellationToken)
        || type == typeof(ReadOnlyMemory<byte>)
        || type == typeof(Memory<byte>);

    private static bool IsForbidden(object value, Type type) =>
        value is SqliteConnection
        || value is SqliteCommand
        || value is SqliteDataReader
        || value is SqliteTransaction
        || value is VaultDatabaseRuntime
        || value is VaultOperationContext
        || value is VaultCommandLease
        || value is SqliteMcDatabaseSession
        || value is VaultKeyMaterial
        || value is VaultKeyEnvelope
        || value is VaultKeyWrap
        || value is VaultCreationResult
        || value is PasswordChangeResult
        || value is DbConnection
        || value is DbCommand
        || value is DbDataReader
        || value is DbTransaction
        || value is Task
        || type == typeof(ValueTask)
        || value is Stream
        || value is Delegate
        || value is Type
        || value is Exception
        || type.Namespace?.StartsWith("SQLitePCL", StringComparison.Ordinal) == true
        || type.Namespace?.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true
        || type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(ValueTask<>)
                || type.GetGenericTypeDefinition() == typeof(Task<>));

    private static bool ImplementsOpenGeneric(Type type, Type openGeneric) =>
        type.GetInterfaces().Any(
            candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == openGeneric);

    private static VaultException Rejected() =>
        new(
            VaultErrorCode.DatabaseError,
            "The database operation returned a value that cannot leave the operation boundary.");
}
