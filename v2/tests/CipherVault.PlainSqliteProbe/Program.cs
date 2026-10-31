using System.Text.Json;
using Microsoft.Data.Sqlite;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: CipherVault.PlainSqliteProbe <db-path>");
    return 2;
}

var querySucceeded = false;
string? exceptionType = null;
try
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = args[0],
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString());
    await connection.OpenAsync();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT count(*) FROM sqlite_master;";
    _ = await command.ExecuteScalarAsync();
    querySucceeded = true;
}
catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
{
    exceptionType = exception.GetType().Name;
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    querySucceeded,
    rejected = !querySucceeded,
    exceptionType
}));
return 0;
