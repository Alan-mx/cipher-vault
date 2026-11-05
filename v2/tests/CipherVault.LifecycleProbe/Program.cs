using System.Security.Cryptography;
using System.Text.Json;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

if (args.Length != 2 || args[0] != "lifecycle")
{
    Console.Error.WriteLine("Usage: lifecycle <vault-dir>");
    Console.Error.WriteLine("Database key hex is read from stdin.");
    return 2;
}

var vaultDirectory = Path.GetFullPath(args[1]);
var databaseKeyHex = Console.ReadLine();
if (string.IsNullOrWhiteSpace(databaseKeyHex))
{
    Console.Error.WriteLine("Database key was not provided on stdin.");
    return 2;
}

var databaseKey = Convert.FromHexString(databaseKeyHex);
var masterKey = RandomNumberGenerator.GetBytes(32);
using var material = new VaultKeyMaterial(Guid.NewGuid(), masterKey, databaseKey);
Directory.CreateDirectory(vaultDirectory);
VaultFilePermissions.ProtectDirectory(vaultDirectory);

try
{
    await using (var connection = await SqliteMcDatabaseFactory.OpenConnectionAsync(
        new DatabaseOpenRequest(vaultDirectory, material.DatabaseKey, CreateIfMissing: true)))
    {
        await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
            connection,
            "CREATE TABLE lifecycle_probe(id INTEGER PRIMARY KEY);",
            CancellationToken.None);
    }

    var firstShutdown = SqliteMcDatabaseFactory.ShutdownProcess();
    var secondShutdown = SqliteMcDatabaseFactory.ShutdownProcess();
    var shutdownCount = SqliteMcDatabaseFactory.ShutdownCallCount;
    VaultErrorCode? reopenError = null;
    try
    {
        await using var connection = await SqliteMcDatabaseFactory.OpenConnectionAsync(
            new DatabaseOpenRequest(vaultDirectory, material.DatabaseKey, CreateIfMissing: false));
    }
    catch (VaultException exception)
    {
        reopenError = exception.Code;
    }

    var result = new
    {
        firstShutdown,
        secondShutdown,
        shutdownCount,
        reopenRejected = reopenError == VaultErrorCode.DatabaseError,
        reopenError = reopenError?.ToString()
    };
    Console.WriteLine(JsonSerializer.Serialize(result));
    return firstShutdown == 0
        && secondShutdown == 0
        && shutdownCount == 1
        && reopenError == VaultErrorCode.DatabaseError
            ? 0
            : 1;
}
finally
{
    CryptographicOperations.ZeroMemory(masterKey);
    CryptographicOperations.ZeroMemory(databaseKey);
}
