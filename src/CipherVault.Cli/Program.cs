using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CipherVault.Auth.Win;
using CipherVault.Core;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Core.Session;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;
using CipherVault.Host.Service;
using CipherVault.Host.ServiceManagement;

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
if (command == "service")
{
    return await RunServiceCommandAsync(args, jsonOptions);
}

var standalone = HasFlag(args, "--standalone");
CliHostSession session;
try
{
    session = await CreateHostSessionAsync(args, standalone);
}
catch (VaultException exception)
{
    Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
    return 2;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

await using var hostSession = session;
var service = hostSession.Client;
var vaultPath = hostSession.VaultPath;
try
{
    return command switch
    {
        "init" => await InitializeAsync(service, Require(vaultPath)),
        "status" => await StatusAsync(service, Require(vaultPath), jsonOptions),
        "backup" => await BackupAsync(service, Require(vaultPath), RequireOption(args, "--to")),
        "restore" => await RestoreAsync(
            service,
            RequireOption(args, "--backup"),
            RequireOption(args, "--to"),
            GetOption(args, "--credential")),
        "verify" => await VerifyAsync(service, Require(vaultPath), jsonOptions),
        "change-password" => await ChangePasswordAsync(service, Require(vaultPath)),
        "rotate-recovery" => await RotateRecoveryAsync(service, Require(vaultPath)),
        "confirm-recovery" => await ConfirmRecoveryAsync(service, Require(vaultPath)),
        "client-list" => await ListClientsAsync(service, jsonOptions),
        "client-add" => await CreateClientAsync(
            service,
            RequireOption(args, "--id"),
            GetOption(args, "--access") ?? "readOnly",
            GetOption(args, "--authorization-mode") ?? "accessGrant",
            jsonOptions),
        "client-rotate" => await RotateClientAsync(
            service,
            RequireOption(args, "--id"),
            jsonOptions),
        "client-delete" => await DeleteClientAsync(
            service,
            RequireOption(args, "--id")),
        "host-stop" => await StopHostAsync(service),
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

static async Task<int> InitializeAsync(
    IHostVaultClient service,
    string vaultPath)
{
    string? password = null;
    if (!service.PromptsForCredentials)
    {
        password = ReadPassword("Vault password: ");
        var confirmation = ReadPassword("Confirm password: ");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Passwords do not match.");
            return 1;
        }
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

static async Task<int> StatusAsync(
    IHostVaultClient service,
    string vaultPath,
    JsonSerializerOptions options)
{
    var fullPath = Path.GetFullPath(vaultPath);
    if (service is NamedPipeHostVaultClient)
    {
        var sessionStatus = await service.GetStatusAsync();
        PrintJson(
            new
            {
                initialized = (bool?)null,
                managedByService = true,
                vaultPath = fullPath,
                session = sessionStatus
            },
            options);
        return 0;
    }
    var initialized =
        File.Exists(Path.Combine(fullPath, "vault.db")) &&
        File.Exists(Path.Combine(fullPath, "vault.keys"));
    var status = await service.GetStatusAsync();
    PrintJson(new { initialized, vaultPath = fullPath, session = status }, options);
    return initialized ? 0 : 1;
}

static async Task<int> BackupAsync(
    IHostVaultClient service,
    string vaultPath,
    string destination)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var result = await service.CreateBackupAsync(destination);
    Console.WriteLine(result.BackupPath);
    WriteAuditWarning(result.AuditRecorded);
    return 0;
}

static async Task<int> RestoreAsync(
    IHostVaultClient service,
    string backup,
    string target,
    string? credentialOption)
{
    var credentialType = ParseCredentialType(credentialOption);
    var credential = service.PromptsForCredentials
        ? string.Empty
        : ReadCredential(credentialType);
    var result = await service.RestoreBackupAsync(
        backup,
        target,
        credential,
        credentialType);
    Console.WriteLine(result.VaultPath);
    WriteAuditWarning(result.AuditRecorded);
    return 0;
}

static async Task<int> VerifyAsync(
    IHostVaultClient service,
    string vaultPath,
    JsonSerializerOptions options)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var report = await service.VerifyVaultAsync();
    PrintJson(report, options);
    return report.Valid ? 0 : 1;
}

static async Task<int> ChangePasswordAsync(
    IHostVaultClient service,
    string vaultPath)
{
    var oldPassword = string.Empty;
    var newPassword = string.Empty;
    if (!service.PromptsForCredentials)
    {
        oldPassword = ReadPassword("Current password: ");
        newPassword = ReadPassword("New password: ");
        var confirmation = ReadPassword("Confirm new password: ");
        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Passwords do not match.");
            return 1;
        }
    }
    await service.ChangePasswordAsync(
        vaultPath,
        oldPassword,
        newPassword);
    Console.WriteLine("Password changed.");
    return 0;
}

static async Task<int> RotateRecoveryAsync(
    IHostVaultClient service,
    string vaultPath)
{
    var password = service.PromptsForCredentials
        ? string.Empty
        : ReadPassword("Current password: ");
    var key = await service.RotateRecoveryKeyAsync(
        vaultPath,
        password);
    Console.WriteLine("NEW OFFLINE RECOVERY KEY - store this outside the Vault:");
    Console.WriteLine(key);
    return await ConfirmRecoveryAsync(service, vaultPath, key);
}

static async Task<int> ConfirmRecoveryAsync(
    IHostVaultClient service,
    string vaultPath,
    string? expectedKey = null)
{
    var confirmation = service.PromptsForCredentials
        ? string.Empty
        : ReadText("Re-enter the recovery key to confirm it was saved: ");
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
        confirmation);
    Console.WriteLine("Recovery key confirmed.");
    return 0;
}

static async Task<int> ListClientsAsync(
    IHostVaultClient service,
    JsonSerializerOptions options)
{
    var clients = await service.ListAgentClientsAsync();
    PrintJson(clients, options);
    return 0;
}

static async Task<int> CreateClientAsync(
    IHostVaultClient service,
    string clientId,
    string accessMode,
    string authorizationMode,
    JsonSerializerOptions options)
{
    if (accessMode is not ("readOnly" or "readWrite"))
    {
        throw new VaultException(
            VaultErrorCode.InvalidInput,
            "--access must be readOnly or readWrite.");
    }
    if (authorizationMode is not ("accessGrant" or "staticToken"))
    {
        throw new VaultException(
            VaultErrorCode.InvalidInput,
            "--authorization-mode must be accessGrant or staticToken.");
    }
    var created = await service.CreateAgentClientAsync(
        clientId,
        accessMode,
        authorizationMode);
    PrintJson(created, options);
    Console.Error.WriteLine(
        "Store the token now. It cannot be displayed again.");
    return 0;
}

static async Task<int> RotateClientAsync(
    IHostVaultClient service,
    string clientId,
    JsonSerializerOptions options)
{
    var rotated = await service.RotateAgentClientAsync(clientId);
    PrintJson(rotated, options);
    Console.Error.WriteLine(
        "Store the new token now. It cannot be displayed again.");
    return 0;
}

static async Task<int> DeleteClientAsync(
    IHostVaultClient service,
    string clientId)
{
    await service.DeleteAgentClientAsync(clientId);
    Console.WriteLine($"client removed: {clientId}");
    return 0;
}

static async Task<int> StopHostAsync(IHostVaultClient service)
{
    await service.StopHostAsync("cli-host-stop");
    Console.WriteLine("Host stopped.");
    return 0;
}

static async Task<int> AuditAsync(
    IHostVaultClient service,
    string vaultPath,
    JsonSerializerOptions options)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var page = await service.GetAuditPageAsync(null, 100);
    PrintJson(page, options);
    return 0;
}

static async Task<int> SearchAsync(
    IHostVaultClient service,
    string vaultPath,
    string query,
    JsonSerializerOptions options)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var page = await service.SearchNotesAsync(query, null, null, 20);
    PrintJson(page, options);
    return 0;
}

static async Task<int> ImportPreviewAsync(
    IHostVaultClient service,
    string vaultPath,
    string source,
    JsonSerializerOptions options)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var preview = await service.PreviewImportAsync(
        source);
    PrintJson(preview, options);
    return 0;
}

static async Task<int> ImportApplyAsync(
    IHostVaultClient service,
    string vaultPath,
    string job,
    JsonSerializerOptions options)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var result = await service.CommitImportAsync(Guid.Parse(job));
    PrintJson(result, options);
    return result.Failed == 0 ? 0 : 1;
}

static async Task<int> ExportAsync(
    IHostVaultClient service,
    string vaultPath,
    string destination,
    JsonSerializerOptions options)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var result = await service.ExportSnapshotAsync(
        destination);
    PrintJson(result, options);
    WriteAuditWarning(result.AuditRecorded);
    return 0;
}

static async Task<int> TrashAsync(
    IHostVaultClient service,
    string vaultPath,
    JsonSerializerOptions options)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var page = await service.ListTrashAsync(null, 100);
    PrintJson(page, options);
    return 0;
}

static async Task<int> RestoreTrashAsync(
    IHostVaultClient service,
    string vaultPath,
    string id,
    string? path)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var revisions = await service.ListTrashAsync(null, 100);
    var item = revisions.Items.SingleOrDefault(value => value.DocumentId == Guid.Parse(id))
        ?? throw new VaultException(VaultErrorCode.NotFound, "Trashed document was not found.");
    var result = await service.RestoreTrashAsync(
        item.DocumentId,
        item.Revision,
        path);
    Console.WriteLine(result.VirtualPath);
    return 0;
}

static async Task<int> PurgeAsync(
    IHostVaultClient service,
    string vaultPath,
    string id)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var confirmation = ReadText("Type PURGE to confirm permanent deletion: ");
    if (!string.Equals(confirmation, "PURGE", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Permanent deletion cancelled.");
        return 1;
    }
    var result = await service.PurgeNoteAsync(Guid.Parse(id));
    Console.WriteLine(result.Purged ? "purged" : "not purged");
    return result.Purged ? 0 : 1;
}

static async Task<int> RevisionsAsync(
    IHostVaultClient service,
    string vaultPath,
    string id,
    JsonSerializerOptions options)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var page = await service.ListRevisionsAsync(Guid.Parse(id), null, 100);
    PrintJson(page, options);
    return 0;
}

static async Task<int> RestoreRevisionAsync(
    IHostVaultClient service,
    string vaultPath,
    string id,
    string revisionId,
    string expected)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var result = await service.RestoreRevisionAsync(
        Guid.Parse(id),
        Guid.Parse(revisionId),
        int.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    Console.WriteLine($"revision={result.Revision}");
    return 0;
}

static async Task<int> RebuildIndexAsync(
    IHostVaultClient service,
    string vaultPath)
{
    await EnsureUnlockedAsync(service, vaultPath);
    var count = await service.RebuildSearchIndexAsync();
    Console.WriteLine($"fts_rows={count}");
    return 0;
}

static async Task EnsureUnlockedAsync(
    IHostVaultClient service,
    string vaultPath)
{
    var status = await service.GetStatusAsync();
    if (status.State is VaultSessionState.Unlocked or VaultSessionState.Expiring)
    {
        return;
    }

    var password = service.PromptsForCredentials
        ? string.Empty
        : ReadPassword("Vault password: ");
    await service.UnlockAsync(
        vaultPath,
        password,
        VaultCredentialType.Password);
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintHelp();
    return 2;
}

static async Task<int> RunServiceCommandAsync(
    string[] arguments,
    JsonSerializerOptions options)
{
    if (arguments.Length < 2)
    {
        Console.Error.WriteLine("Missing service command.");
        return 2;
    }

    var action = arguments[1].ToLowerInvariant();
    try
    {
        switch (action)
        {
            case "status":
                var status = HostServiceStatusReader.Read(
                    StrictServiceHost.ServiceName,
                    CipherVaultServicePaths.Default.PublicManifestPath,
                    new WindowsServiceQuery());
                PrintJson(status, options);
                return status.Installed ? 0 : 1;
            case "start":
                return await RunServiceOperationAsync(
                    arguments,
                    options,
                    static (operations, cancellationToken) =>
                        operations.StartAsync(
                            StrictServiceHost.ServiceName,
                            cancellationToken));
            case "stop":
                return await RunServiceOperationAsync(
                    arguments,
                    options,
                    static (operations, cancellationToken) =>
                        operations.StopAsync(
                            StrictServiceHost.ServiceName,
                            cancellationToken));
            case "uninstall":
                return await RunServiceOperationAsync(
                    arguments,
                    options,
                    static (operations, cancellationToken) =>
                        operations.UninstallAsync(
                            StrictServiceHost.ServiceName,
                            cancellationToken));
            case "install":
                return await RunServiceInstallAsync(arguments, options);
            default:
                Console.Error.WriteLine($"Unknown service command: {action}");
                return 2;
        }
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
}

static async Task<int> RunServiceInstallAsync(
    string[] arguments,
    JsonSerializerOptions options)
{
    var paths = CipherVaultServicePaths.Default;
    var currentSid = WindowsIdentity.GetCurrent().User
        ?? throw new VaultException(
            VaultErrorCode.StorageError,
            "Unable to resolve the current Windows user.");
    var installOptions = new HostServiceInstallOptions(
        GetOption(arguments, "--owner-sid") ?? currentSid.Value,
        GetOption(arguments, "--signer-thumbprint"),
        GetOption(arguments, "--host-exe") ??
            Path.Combine(paths.InstallRoot, "CipherVault.Host.exe"),
        GetOption(arguments, "--ui-exe") ??
            Path.Combine(paths.InstallRoot, "CipherVault.Ui.Wpf.exe"),
        GetOption(arguments, "--cli-exe") ??
            Path.Combine(paths.InstallRoot, "CipherVault.Cli.exe"),
        GetOption(arguments, "--broker-exe") ??
            Path.Combine(paths.InstallRoot, "CipherVault.Auth.Broker.exe"),
        GetOption(arguments, "--mcp-exe"),
        StartService: !HasFlag(arguments, "--no-start"));
    using var cancellationTokenSource = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArguments) =>
    {
        eventArguments.Cancel = true;
        cancellationTokenSource.Cancel();
    };
    var installer = new HostServiceInstaller(
        new WindowsServiceQuery(),
        new ScExeCommandRunner(),
        new WindowsServiceSidResolver(),
        new SignedServiceImageInspector(),
        new WindowsServiceInstallAcl());
    var result = await installer.InstallAsync(
        installOptions,
        paths,
        cancellationTokenSource.Token);
    PrintJson(result, options);
    return 0;
}

static async Task<int> RunServiceOperationAsync(
    string[] arguments,
    JsonSerializerOptions options,
    Func<
        WindowsServiceOperations,
        CancellationToken,
        Task<WindowsServiceSnapshot>> operation)
{
    using var cancellationTokenSource = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArguments) =>
    {
        eventArguments.Cancel = true;
        cancellationTokenSource.Cancel();
    };
    var operations = new WindowsServiceOperations(
        new WindowsServiceQuery(),
        new ScExeCommandRunner());
    var snapshot = await operation(
        operations,
        cancellationTokenSource.Token);
    PrintJson(
        new
        {
            serviceName = StrictServiceHost.ServiceName,
            snapshot.Installed,
            snapshot.State,
            snapshot.StartMode,
            command = arguments[1].ToLowerInvariant()
        },
        options);
    return snapshot.Installed ? 0 : 1;
}

static async Task<CliHostSession> CreateHostSessionAsync(
    string[] arguments,
    bool standalone)
{
    if (standalone)
    {
        var configuration = CipherVaultHostConfiguration.Load(
            CreateHostOverrides(arguments),
            GetOption(arguments, "--config"));
        VaultModeStore.EnsureCompatibilityAllowed(configuration.VaultPath);
        var ownership = await VaultOwnershipLock.AcquireAsync(
            configuration.VaultPath);
        try
        {
            var vault = new VaultService(
                new CliActivityProvider(),
                sessionOptions: configuration.SessionOptions);
            return new CliHostSession(
                configuration.VaultPath,
                new StandaloneHostVaultClient(vault, VaultActorKind.UserCli),
                ownership);
        }
        catch
        {
            await ownership.DisposeAsync();
            throw;
        }
    }

    var compatibilityConfigPath = GetOption(arguments, "--config");
    if (compatibilityConfigPath is not null)
    {
        var hostConfiguration = CipherVaultHostConfiguration.Load(
            CreateHostOverrides(arguments),
            compatibilityConfigPath);
        var client = await HttpHostVaultClient.CreateAsync(
            hostConfiguration,
            HostAdminClientKind.Cli);
        return new CliHostSession(
            hostConfiguration.VaultPath,
            client,
            ownership: null);
    }

    var paths = CipherVaultServicePaths.Default;
    var manifestPath = GetOption(arguments, "--manifest") ??
        paths.PublicManifestPath;
    var manifest = CipherVaultServiceConfiguration.LoadInstallManifest(
        manifestPath);
    var currentUser = WindowsIdentity.GetCurrent().User
        ?? throw new VaultException(
            VaultErrorCode.StorageError,
            "Unable to resolve the current Windows user.");
    if (!string.Equals(
            currentUser.Value,
            manifest.OwnerSid,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new VaultException(
            VaultErrorCode.AccessDenied,
            "The current Windows user is not the CipherVault owner.");
    }
    var adminPipe = manifest.Pipes?.Admin;
    if (string.IsNullOrWhiteSpace(adminPipe))
    {
        throw new VaultException(
            VaultErrorCode.InvalidInput,
            "Install manifest Admin pipe is required.");
    }
    using var brokerProcess = StrictServiceBrokerLauncher.EnsureStarted(
        manifest,
        manifestPath);
    var options = StrictServiceClientPolicyFactory.CreateAdminOptions(
        manifest,
        adminPipe,
        currentUser.Value,
        (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId,
        VerifiedProcessKind.AdminCli);
    var strictClient = await NamedPipeHostVaultClient.CreateAsync(options);
    return new CliHostSession(paths.VaultPath, strictClient, ownership: null);
}

static bool HasFlag(string[] arguments, string flag)
{
    return arguments.Any(argument =>
        string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));
}

static string? GetOption(string[] arguments, string option)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase))
        {
            if (arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"Option {option} requires a value.");
            }
            return arguments[index + 1];
        }
    }
    if (arguments.Any(argument =>
            string.Equals(argument, option, StringComparison.OrdinalIgnoreCase)))
    {
        throw new VaultException(
            VaultErrorCode.InvalidInput,
            $"Option {option} requires a value.");
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

static VaultCredentialType ParseCredentialType(string? value)
{
    return value?.ToLowerInvariant() switch
    {
        null or "" or "password" => VaultCredentialType.Password,
        "recovery-key" or "recovery" => VaultCredentialType.RecoveryKey,
        _ => throw new VaultException(
            VaultErrorCode.InvalidInput,
            "--credential must be password or recovery-key.")
    };
}

static string ReadCredential(VaultCredentialType credentialType)
{
    return credentialType switch
    {
        VaultCredentialType.Password => ReadPassword("Vault password: "),
        VaultCredentialType.RecoveryKey => NormalizeRecoveryKey(
            ReadPassword("Offline recovery key: ")),
        _ => throw new VaultException(
            VaultErrorCode.InvalidInput,
            "Unsupported credential type.")
    };
}

static CipherVaultHostConfigOverrides CreateHostOverrides(string[] arguments)
{
    return new CipherVaultHostConfigOverrides
    {
        VaultPath = GetOption(arguments, "--vault"),
        IdleMinutes = ParseOptionalInteger(
            GetOption(arguments, "--idle-minutes"),
            "--idle-minutes"),
        HardHours = ParseOptionalInteger(
            GetOption(arguments, "--hard-hours"),
            "--hard-hours")
    };
}

static int? ParseOptionalInteger(
    string? value,
    string option)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }
    if (!int.TryParse(value, out var parsed))
    {
        throw new VaultException(
            VaultErrorCode.InvalidInput,
            $"{option} must be an integer.");
    }
    return parsed;
}

static void PrintJson<T>(T value, JsonSerializerOptions options)
{
    Console.WriteLine(JsonSerializer.Serialize(value, options));
}

static void WriteAuditWarning(bool auditRecorded)
{
    if (!auditRecorded)
    {
        Console.Error.WriteLine(
            "warning: operation completed, but its audit event was not recorded.");
    }
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        CipherVault CLI

        Commands:
          service status      Inspect the Windows service and install manifest
          service install     Install strict service from signed Program Files binaries
          service start       Start the installed Windows service
          service stop        Stop the Windows service
          service uninstall   Remove the service registration and retain data
          init                --vault <directory>
          status              --vault <directory>
          backup              --vault <directory> --to <file.cvbackup>
          restore             --backup <file.cvbackup> --to <new-directory> [--credential password|recovery-key]
          verify              --vault <directory>
          change-password     --vault <directory>
          rotate-recovery     --vault <directory>
          confirm-recovery    --vault <directory>
          client-list
          client-add          --id <client-id> [--access readOnly|readWrite] [--authorization-mode accessGrant|staticToken]
          client-rotate       --id <client-id>
          client-delete       --id <client-id>
          host-stop
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

        Session options:
          --manifest <file>        Defaults to %ProgramData%\CipherVault\install-public.json
          --config <file>          Explicit compatibility Host configuration
          --standalone             Explicit direct mode; requires Host to be stopped
          --idle-minutes <1-1440>  Defaults to 15
          --hard-hours <1-168>     Defaults to 4 and must cover the idle timeout
        """);
}

file sealed class CliActivityProvider : ISystemActivityProvider
{
    public SystemActivitySnapshot GetSnapshot()
    {
        return new SystemActivitySnapshot(TimeSpan.Zero, false);
    }
}

file sealed class CliHostSession : IAsyncDisposable
{
    private VaultOwnershipLock? _ownership;

    public CliHostSession(
        string vaultPath,
        IHostVaultClient client,
        VaultOwnershipLock? ownership)
    {
        VaultPath = vaultPath;
        Client = client;
        _ownership = ownership;
    }

    public string VaultPath { get; }
    public IHostVaultClient Client { get; }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        var ownership = Interlocked.Exchange(ref _ownership, null);
        if (ownership is not null)
        {
            await ownership.DisposeAsync();
        }
    }
}
