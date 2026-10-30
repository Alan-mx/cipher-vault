using System.Text.Json;
using Microsoft.Data.Sqlite;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: CipherVault.PlainSqliteProbe <db-path>");
    return 2;
}

var started = false;
var querySucceeded = false;
string? exceptionType = null;
string? errorSummary = null;

try
{
    started = true;
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = args[0],
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT count(*) FROM sqlite_master;";
    _ = await command.ExecuteScalarAsync();
    querySucceeded = true;
}
catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
{
    exceptionType = exception.GetType().Name;
    errorSummary = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    started,
    querySucceeded,
    rejected = !querySucceeded,
    exceptionType,
    errorSummary
}));

return 0;
