using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalVault.Auth.Win;
using PersonalVault.Core;
using PersonalVault.Core.Documents;
using PersonalVault.Core.Errors;
using PersonalVault.Core.Session;

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

var command = args[0].ToLowerInvariant();
var vaultPath = GetOption(args, "--vault");
if (command != "restore" && string.IsNullOrWhiteSpace(vaultPath))
{
    Console.Error.WriteLine("Missing required option: --vault <directory>");
    return 2;
}

await using var service = new VaultService(new CliActivityProvider());
try
{
    return command switch
    {
        "init" => await InitializeAsync(service, Require(vaultPath)),
        "status" => await StatusAsync(service, Require(vaultPath), jsonOptions),
        "backup" => await BackupAsync(service, Require(vaultPath), RequireOption(args, "--to")),
        "restore" => await RestoreAsync(service, RequireOption(args, "--backup"), RequireOption(args, "--to")),
        "verify" => await VerifyAsync(service, Require(vaultPath), jsonOptions),
        "change-password" => await ChangePasswordAsync(service, Require(vaultPath)),
        "rotate-recovery" => await RotateRecoveryAsync(service, Require(vaultPath)),
        "confirm-recovery" => await ConfirmRecoveryAsync(service, Require(vaultPath)),
        "audit" => await AuditAsync(service, Require(vaultPath), jsonOptions),
        "search" => await SearchAsync(service, Require(vaultPath), RequireOption(args, "--query"), jsonOptions),
        "import-preview" => await ImportPreviewAsync(service, Require(vaultPath), RequireOption(args, "--source"), jsonOptions),
        "import-apply" => await ImportApplyAsync(service, Require(vaultPath), RequireOption(args, "--job"), jsonOptions),
        "export" => await ExportAsync(service, Require(vaultPath), RequireOption(args, "--to"), jsonOptions),
        "trash" => await TrashAsync(service, Require(vaultPath), jsonOptions),
        "restore-trash" => await RestoreTrashAsync(service, Require(vaultPath), RequireOption(args, "--id"), GetOption(args, "--path")),
        "purge" => await PurgeAsync(service, Require(vaultPath), RequireOption(args, "--id")),
        "revisions" => await RevisionsAsync(service, Require(vaultPath), RequireOption(args, "--id"), jsonOptions),
        "restore-revision" => await RestoreRevisionAsync(service, Require(vaultPath), RequireOption(args, "--id"), RequireOption(args, "--revision-id"), RequireOption(args, "--expected")),
        "rebuild-index" => await RebuildIndexAsync(service, Require(vaultPath)),
        _ => UnknownCommand(command)
    };
}
catch (VaultException exception)
{
    Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"storage_error: {exception.Message}");
    return 1;
}

static async Task<int> InitializeAsync(VaultService service, string vaultPath)
{
    var password = ReadPassword("Vault password: ");
    var confirmation = ReadPassword("Confirm password: ");
    if (!string.Equals(password, confirmation, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Passwords do not match.");
        return 1;
    }
    var result = await service.InitializeAsync(vaultPath, password);
    Console.WriteLine($"Vault initialized: {result.VaultPath}");
    Console.WriteLine();
    Console.WriteLine("OFFLINE RECOVERY KEY - store this outside the Vault:");
    Console.WriteLine(result.RecoveryKey);
    Console.WriteLine();
    Console.WriteLine("Losing both the password and this key makes recovery impossible.");
    return await ConfirmRecoveryAsync(service, vaultPath, result.RecoveryKey);
}

static async Task<int> StatusAsync(VaultService service, string vaultPath, JsonSerializerOptions options)
{
    var fullPath = Path.GetFullPath(vaultPath);
    var initialized =
        File.Exists(Path.Combine(fullPath, "vault.db")) &&
        File.Exists(Path.Combine(fullPath, "vault.keys"));
    var status = await service.GetStatusAsync();
    PrintJson(new { initialized, vaultPath = fullPath, session = status }, options);
    return initialized ? 0 : 1;
}

static async Task<int> BackupAsync(VaultService service, string vaultPath, string destination)
{
    await UnlockAsync(service, vaultPath);
    var result = await service.CreateBackupAsync(destination, VaultActorKind.UserCli);
    Console.WriteLine(result.BackupPath);
    return 0;
}

static async Task<int> RestoreAsync(VaultService service, string backup, string target)
{
    var password = ReadPassword("Vault password: ");
    var result = await service.RestoreBackupAsync(
        backup,
        target,
        password,
        VaultCredentialType.Password,
        VaultActorKind.UserCli);
    Console.WriteLine(result.VaultPath);
    return 0;
}

static async Task<int> VerifyAsync(VaultService service, string vaultPath, JsonSerializerOptions options)
{
    await UnlockAsync(service, vaultPath);
    var report = await service.VerifyVaultAsync();
    PrintJson(report, options);
    return report.Valid ? 0 : 1;
}

static async Task<int> ChangePasswordAsync(VaultService service, string vaultPath)
{
    var oldPassword = ReadPassword("Current password: ");
    var newPassword = ReadPassword("New password: ");
    var confirmation = ReadPassword("Confirm new password: ");
    if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Passwords do not match.");
        return 1;
    }
    await service.ChangePasswordAsync(
        vaultPath,
        oldPassword,
        newPassword,
        VaultActorKind.UserCli);
    Console.WriteLine("Password changed.");
    return 0;
}

static async Task<int> RotateRecoveryAsync(VaultService service, string vaultPath)
{
    var password = ReadPassword("Current password: ");
    var key = await service.RotateRecoveryKeyAsync(
        vaultPath,
        password,
        VaultActorKind.UserCli);
    Console.WriteLine("NEW OFFLINE RECOVERY KEY - store this outside the Vault:");
    Console.WriteLine(key);
    return await ConfirmRecoveryAsync(service, vaultPath, key);
}

static async Task<int> ConfirmRecoveryAsync(
    VaultService service,
    string vaultPath,
    string? expectedKey = null)
{
    var confirmation = ReadText("Re-enter the recovery key to confirm it was saved: ");
    if (expectedKey is not null &&
        !string.Equals(
            NormalizeRecoveryKey(confirmation),
            NormalizeRecoveryKey(expectedKey),
            StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Recovery key confirmation did not match.");
        return 1;
    }

    await service.ConfirmRecoveryKeyAsync(
        vaultPath,
        confirmation,
        VaultActorKind.UserCli);
    Console.WriteLine("Recovery key confirmed.");
    return 0;
}

static async Task<int> AuditAsync(VaultService service, string vaultPath, JsonSerializerOptions options)
{
    await UnlockAsync(service, vaultPath);
    var page = await service.GetAuditPageAsync(null, 100);
    PrintJson(page, options);
    return 0;
}

static async Task<int> SearchAsync(
    VaultService service,
    string vaultPath,
    string query,
    JsonSerializerOptions options)
{
    await UnlockAsync(service, vaultPath);
    var page = await service.SearchNotesAsync(query, null, null, 20);
    PrintJson(page, options);
    return 0;
}

static async Task<int> ImportPreviewAsync(
    VaultService service,
    string vaultPath,
    string source,
    JsonSerializerOptions options)
{
    await UnlockAsync(service, vaultPath);
    var preview = await service.PreviewImportAsync(
        source,
        VaultActorKind.UserCli);
    PrintJson(preview, options);
    return 0;
}

static async Task<int> ImportApplyAsync(
    VaultService service,
    string vaultPath,
    string job,
    JsonSerializerOptions options)
{
    await UnlockAsync(service, vaultPath);
    var result = await service.CommitImportAsync(
        Guid.Parse(job),
        VaultActorKind.UserCli);
    PrintJson(result, options);
    return result.Failed == 0 ? 0 : 1;
}

static async Task<int> ExportAsync(
    VaultService service,
    string vaultPath,
    string destination,
    JsonSerializerOptions options)
{
    await UnlockAsync(service, vaultPath);
    var result = await service.ExportSnapshotAsync(
        destination,
        VaultActorKind.UserCli);
    PrintJson(result, options);
    return 0;
}

static async Task<int> TrashAsync(
    VaultService service,
    string vaultPath,
    JsonSerializerOptions options)
{
    await UnlockAsync(service, vaultPath);
    var page = await service.ListTrashAsync(null, 100);
    PrintJson(page, options);
    return 0;
}

static async Task<int> RestoreTrashAsync(
    VaultService service,
    string vaultPath,
    string id,
    string? path)
{
    await UnlockAsync(service, vaultPath);
    var revisions = await service.ListTrashAsync(null, 100);
    var item = revisions.Items.SingleOrDefault(value => value.DocumentId == Guid.Parse(id))
        ?? throw new VaultException(VaultErrorCode.NotFound, "Trashed document was not found.");
    var result = await service.RestoreTrashAsync(
        item.DocumentId,
        item.Revision,
        path,
        VaultActorKind.UserCli);
    Console.WriteLine(result.VirtualPath);
    return 0;
}

static async Task<int> PurgeAsync(VaultService service, string vaultPath, string id)
{
    await UnlockAsync(service, vaultPath);
    var confirmation = ReadText("Type PURGE to confirm permanent deletion: ");
    if (!string.Equals(confirmation, "PURGE", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Permanent deletion cancelled.");
        return 1;
    }
    var result = await service.PurgeNoteAsync(
        Guid.Parse(id),
        VaultActorKind.UserCli);
    Console.WriteLine(result.Purged ? "purged" : "not purged");
    return result.Purged ? 0 : 1;
}

static async Task<int> RevisionsAsync(
    VaultService service,
    string vaultPath,
    string id,
    JsonSerializerOptions options)
{
    await UnlockAsync(service, vaultPath);
    var page = await service.ListRevisionsAsync(Guid.Parse(id), null, 100);
    PrintJson(page, options);
    return 0;
}

static async Task<int> RestoreRevisionAsync(
    VaultService service,
    string vaultPath,
    string id,
    string revisionId,
    string expected)
{
    await UnlockAsync(service, vaultPath);
    var result = await service.RestoreRevisionAsync(
        Guid.Parse(id),
        Guid.Parse(revisionId),
        int.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
        VaultActorKind.UserCli);
    Console.WriteLine($"revision={result.Revision}");
    return 0;
}

static async Task<int> RebuildIndexAsync(VaultService service, string vaultPath)
{
    await UnlockAsync(service, vaultPath);
    var count = await service.RebuildSearchIndexAsync(VaultActorKind.UserCli);
    Console.WriteLine($"fts_rows={count}");
    return 0;
}

static async Task UnlockAsync(VaultService service, string vaultPath)
{
    var password = ReadPassword("Vault password: ");
    await service.UnlockAsync(vaultPath, password, VaultCredentialType.Password);
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintHelp();
    return 2;
}

static string? GetOption(string[] arguments, string option)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }
    return null;
}

static string RequireOption(string[] arguments, string option)
{
    return GetOption(arguments, option)
        ?? throw new VaultException(VaultErrorCode.InvalidInput, $"Missing option: {option}");
}

static string Require(string? value)
{
    return value ?? throw new VaultException(VaultErrorCode.InvalidInput, "Missing required value.");
}

static string ReadText(string prompt)
{
    Console.Write(prompt);
    return Console.ReadLine() ?? string.Empty;
}

static string ReadPassword(string prompt)
{
    if (Console.IsInputRedirected)
    {
        Console.WriteLine(prompt);
        return Console.ReadLine() ?? string.Empty;
    }

    Console.Write(prompt);
    var value = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return value.ToString();
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length > 0)
            {
                value.Length--;
            }
            continue;
        }
        if (!char.IsControl(key.KeyChar))
        {
            value.Append(key.KeyChar);
        }
    }
}

static string NormalizeRecoveryKey(string value)
{
    return new string(
        value
            .Where(character => !char.IsWhiteSpace(character) && character != '-')
            .Select(char.ToUpperInvariant)
            .ToArray());
}

static void PrintJson<T>(T value, JsonSerializerOptions options)
{
    Console.WriteLine(JsonSerializer.Serialize(value, options));
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        Personal Vault CLI

        Commands:
          init                --vault <directory>
          status              --vault <directory>
          backup              --vault <directory> --to <file.pvbackup>
          restore             --backup <file.pvbackup> --to <new-directory>
          verify              --vault <directory>
          change-password     --vault <directory>
          rotate-recovery     --vault <directory>
          confirm-recovery    --vault <directory>
          audit               --vault <directory>
          search              --vault <directory> --query <text>
          import-preview      --vault <directory> --source <directory>
          import-apply        --vault <directory> --job <job-id>
          export              --vault <directory> --to <new-directory>
          trash               --vault <directory>
          restore-trash       --vault <directory> --id <document-id> [--path <virtual-path>]
          purge               --vault <directory> --id <document-id>
          revisions           --vault <directory> --id <document-id>
          restore-revision    --vault <directory> --id <document-id> --revision-id <id> --expected <revision>
          rebuild-index       --vault <directory>
        """);
}

file sealed class CliActivityProvider : ISystemActivityProvider
{
    public SystemActivitySnapshot GetSnapshot()
    {
        return new SystemActivitySnapshot(TimeSpan.Zero, false);
    }
}
