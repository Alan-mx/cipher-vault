using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Host.Contracts;
using CipherVault.Host.Service;
using CipherVault.Security.Win;

namespace CipherVault.Host.ServiceManagement;

public sealed record HostServiceInstallOptions(
    string OwnerSid,
    string? SignerThumbprint,
    string HostExecutablePath,
    string? UiExecutablePath,
    string? CliExecutablePath,
    string? BrokerExecutablePath,
    string? McpExecutablePath,
    bool StartService = true);

public sealed record HostServiceInstallResult(
    Guid InstallId,
    string ServiceName,
    string ServiceSid,
    string HostExecutablePath,
    string ServiceConfigPath,
    string PublicManifestPath,
    string VaultModePath,
    bool Started);

public sealed record ServiceImageEvidence(
    string ImageSha256,
    string SignerThumbprint,
    string SignerSubject);

public interface IServiceImageInspector
{
    ServiceImageEvidence Inspect(string imagePath);
}

public sealed class SignedServiceImageInspector : IServiceImageInspector
{
    public ServiceImageEvidence Inspect(string imagePath)
    {
        var fullPath = Path.GetFullPath(imagePath);
        if (!WindowsProcessIdentityVerifier.TryGetAuthenticodeSigner(
                fullPath,
                out var thumbprint,
                out var subject) ||
            string.IsNullOrWhiteSpace(thumbprint) ||
            string.IsNullOrWhiteSpace(subject))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Service image is not validly signed: {fullPath}");
        }
        return new ServiceImageEvidence(
            WindowsProcessIdentityVerifier.ComputeFileSha256(fullPath),
            thumbprint,
            subject);
    }
}

public interface IServiceSidResolver
{
    SecurityIdentifier Resolve(string serviceName);
}

public sealed class WindowsServiceSidResolver : IServiceSidResolver
{
    public SecurityIdentifier Resolve(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        try
        {
            var account = new NTAccount(
                "NT SERVICE",
                serviceName);
            return (SecurityIdentifier)account.Translate(
                typeof(SecurityIdentifier));
        }
        catch (Exception exception) when (
            exception is IdentityNotMappedException or
            SystemException or
            UnauthorizedAccessException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                $"Service account SID could not be resolved: {serviceName}",
                exception);
        }
    }
}

public interface IServiceInstallAcl
{
    void Apply(
        CipherVaultServicePaths paths,
        SecurityIdentifier serviceSid,
        SecurityIdentifier ownerSid);
}

public sealed class WindowsServiceInstallAcl : IServiceInstallAcl
{
    public void Apply(
        CipherVaultServicePaths paths,
        SecurityIdentifier serviceSid,
        SecurityIdentifier ownerSid)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(serviceSid);
        ArgumentNullException.ThrowIfNull(ownerSid);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            null);
        VaultFileAccessRule[] protectedDirectoryRules =
        [
            new(serviceSid, FileSystemRights.FullControl, InheritToChildren: true),
            new(administrators, FileSystemRights.FullControl, InheritToChildren: true),
            new(system, FileSystemRights.FullControl, InheritToChildren: true)
        ];
        VaultFileAccessRule[] protectedFileRules =
        [
            new(serviceSid, FileSystemRights.FullControl),
            new(administrators, FileSystemRights.FullControl),
            new(system, FileSystemRights.FullControl)
        ];

        foreach (var directory in new[]
                 {
                     paths.DataRoot,
                     paths.VaultPath,
                     paths.LogsPath,
                     paths.LocksPath,
                     paths.TransferRoot
                 })
        {
            Directory.CreateDirectory(directory);
            VaultFileSecurity.ProtectDirectoryWithRules(
                directory,
                protectedDirectoryRules);
            VaultFileSecurity.SetDirectoryOwner(directory, serviceSid);
        }

        VaultFileSecurity.ProtectFileWithRules(
            paths.ServiceConfigPath,
            protectedFileRules);
        VaultFileSecurity.SetFileOwner(
            paths.ServiceConfigPath,
            serviceSid);
        VaultFileSecurity.ProtectFileWithRules(
            paths.PublicManifestPath,
            [
                .. protectedFileRules,
                new VaultFileAccessRule(
                    ownerSid,
                    FileSystemRights.ReadAndExecute)
            ]);
        VaultFileSecurity.SetFileOwner(
            paths.PublicManifestPath,
            serviceSid);
        VaultFileSecurity.ProtectFileWithRules(
            paths.ModePath,
            protectedFileRules);
        VaultFileSecurity.SetFileOwner(paths.ModePath, serviceSid);
    }
}

public sealed class HostServiceInstaller(
    IWindowsServiceQuery serviceQuery,
    IWindowsServiceCommandRunner commandRunner,
    IServiceSidResolver serviceSidResolver,
    IServiceImageInspector imageInspector,
    IServiceInstallAcl acl)
{
    public const string ServiceAccount = @"NT SERVICE\CipherVaultHost";
    private const string DisplayName = "CipherVault Host";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    public async Task<HostServiceInstallResult> InstallAsync(
        HostServiceInstallOptions options,
        CipherVaultServicePaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        var ownerSid = ParseSid(options.OwnerSid, "owner SID");
        var images = InspectImages(options, paths);
        var signerThumbprint = ResolveSignerThumbprint(
            options.SignerThumbprint,
            images);
        EnsureFreshInstall(paths);
        var current = serviceQuery.GetStatus(StrictServiceHost.ServiceName);
        if (current.Installed)
        {
            throw new VaultException(
                VaultErrorCode.AlreadyExists,
                $"Windows service is already installed: {StrictServiceHost.ServiceName}");
        }

        var installId = Guid.NewGuid();
        var serviceConfigPath = paths.ServiceConfigPath;
        var serviceConfiguration = CreateServiceConfiguration(
            options,
            paths,
            signerThumbprint,
            images);
        CipherVaultServiceConfiguration.Validate(serviceConfiguration);
        var serviceCreated = false;
        var writtenFiles = new List<string>();
        try
        {
            var binaryPath = Quote(options.HostExecutablePath) +
                " --service --config " +
                Quote(serviceConfigPath);
            var create = await commandRunner.RunAsync(
                [
                    "create",
                    StrictServiceHost.ServiceName,
                    "binPath=",
                    binaryPath,
                    "start=",
                    "auto",
                    "obj=",
                    ServiceAccount,
                    "DisplayName=",
                    DisplayName
                ],
                cancellationToken).ConfigureAwait(false);
            EnsureCommandSucceeded(create, "create");
            serviceCreated = true;

            var sidType = await commandRunner.RunAsync(
                [
                    "sidtype",
                    StrictServiceHost.ServiceName,
                    "unrestricted"
                ],
                cancellationToken).ConfigureAwait(false);
            EnsureCommandSucceeded(sidType, "configure the service SID");
            var description = await commandRunner.RunAsync(
                [
                    "description",
                    StrictServiceHost.ServiceName,
                    DisplayName
                ],
                cancellationToken).ConfigureAwait(false);
            EnsureCommandSucceeded(description, "configure the service description");
            var serviceSid = serviceSidResolver.Resolve(
                StrictServiceHost.ServiceName);
            var installManifest = CreateInstallManifest(
                serviceConfiguration,
                serviceSid,
                installId,
                images);
            CipherVaultServiceConfiguration.Validate(installManifest);
            var modeManifest = new CipherVaultVaultModeManifest
            {
                Format = CipherVaultServiceConfiguration.VaultModeFormat,
                FormatVersion =
                    CipherVaultServiceConfiguration.VaultModeFormatVersion,
                Mode = CipherVaultServiceConfiguration.StrictModeValue,
                OwnerSid = serviceConfiguration.OwnerSid,
                InstallId = installId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            CipherVaultServiceConfiguration.Validate(modeManifest);

            Directory.CreateDirectory(paths.DataRoot);
            WriteJson(serviceConfigPath, serviceConfiguration, writtenFiles);
            WriteJson(paths.PublicManifestPath, installManifest, writtenFiles);
            VaultModeStore.Write(paths.ModePath, modeManifest, cancellationToken);
            writtenFiles.Add(paths.ModePath);
            acl.Apply(paths, serviceSid, ownerSid);

            var started = false;
            if (options.StartService)
            {
                var operations = new WindowsServiceOperations(
                    serviceQuery,
                    commandRunner);
                _ = await operations.StartAsync(
                    StrictServiceHost.ServiceName,
                    cancellationToken).ConfigureAwait(false);
                started = true;
            }
            return new HostServiceInstallResult(
                installId,
                StrictServiceHost.ServiceName,
                serviceSid.Value,
                Path.GetFullPath(options.HostExecutablePath),
                serviceConfigPath,
                paths.PublicManifestPath,
                paths.ModePath,
                started);
        }
        catch
        {
            if (serviceCreated)
            {
                try
                {
                    _ = await commandRunner.RunAsync(
                        ["stop", StrictServiceHost.ServiceName],
                        CancellationToken.None).ConfigureAwait(false);
                    _ = await commandRunner.RunAsync(
                        ["delete", StrictServiceHost.ServiceName],
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (VaultException)
                {
                }
            }
            foreach (var file in writtenFiles)
            {
                TryDeleteFile(file);
            }
            RemoveEmptyInstallDirectories(paths);
            throw;
        }
    }

    private List<ServiceImageEvidence> InspectImages(
        HostServiceInstallOptions options,
        CipherVaultServicePaths paths)
    {
        if (string.IsNullOrWhiteSpace(options.UiExecutablePath) &&
            string.IsNullOrWhiteSpace(options.CliExecutablePath))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "At least one signed Admin UI or CLI image is required.");
        }
        if (string.IsNullOrWhiteSpace(options.BrokerExecutablePath))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "A signed Auth Broker image is required.");
        }
        var result = new List<ServiceImageEvidence>();
        foreach (var imagePath in EnumerateImagePaths(options))
        {
            var fullPath = Path.GetFullPath(imagePath);
            if (!IsUnderPath(fullPath, paths.InstallRoot))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"Service image must be under {paths.InstallRoot}: {fullPath}");
            }
            if (!File.Exists(fullPath))
            {
                throw new VaultException(
                    VaultErrorCode.NotFound,
                    $"Service image was not found: {fullPath}");
            }
            result.Add(imageInspector.Inspect(fullPath));
        }
        return result;
    }

    private static string ResolveSignerThumbprint(
        string? configuredThumbprint,
        IReadOnlyList<ServiceImageEvidence> images)
    {
        var expected = string.IsNullOrWhiteSpace(configuredThumbprint)
            ? images[0].SignerThumbprint
            : configuredThumbprint.Trim();
        ValidateSha256(expected, "signer thumbprint");
        if (images.Any(image =>
                !string.Equals(
                    image.SignerThumbprint,
                    expected,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "All service images must have the configured signer.");
        }
        return expected;
    }

    private static CipherVaultServiceConfigFile CreateServiceConfiguration(
        HostServiceInstallOptions options,
        CipherVaultServicePaths paths,
        string signerThumbprint,
        IReadOnlyList<ServiceImageEvidence> images)
    {
        var imagePaths = EnumerateImagePaths(options).ToArray();
        return new CipherVaultServiceConfigFile
        {
            ConfigVersion = CipherVaultServiceConfiguration.CurrentConfigVersion,
            SecurityMode = CipherVaultServiceConfiguration.StrictModeValue,
            OwnerSid = new SecurityIdentifier(options.OwnerSid).Value,
            ServiceAccount = ServiceAccount,
            VaultPath = paths.VaultPath,
            DataRoot = paths.DataRoot,
            Session = new CipherVaultSessionConfigFile
            {
                IdleMinutes = 15,
                HardHours = 4
            },
            Host = new CipherVaultServiceHostConfigFile
            {
                McpPath = "/mcp",
                MaxRequestBodyBytes = 4 * 1024 * 1024,
                AllowedOrigins = []
            },
            Pipes = FixedPipes(),
            ProcessPolicy = new CipherVaultProcessPolicyConfigFile
            {
                AllowedSignerThumbprints = [signerThumbprint],
                AllowedImages = imagePaths
                    .Select((path, index) => new CipherVaultImagePolicyFile
                    {
                        Kind = ImageKind(index),
                        Path = Path.GetFullPath(path),
                        Sha256 = images[index].ImageSha256,
                        SignerThumbprint = signerThumbprint,
                        SignerSubject = images[index].SignerSubject
                    })
                    .ToArray(),
                RequireSha256 = true
            },
            Agent = new CipherVaultAgentRuntimeConfigFile
            {
                StrictTransport = "stdio-bridge",
                CompatibilityHttpEnabled = false,
                Bridges = [],
                Clients = []
            }
        };
    }

    private static CipherVaultInstallPublicManifest CreateInstallManifest(
        CipherVaultServiceConfigFile configuration,
        SecurityIdentifier serviceSid,
        Guid installId,
        IReadOnlyList<ServiceImageEvidence> images)
    {
        var imagePaths = EnumerateImagePathsFromConfiguration(configuration)
            .ToArray();
        return new CipherVaultInstallPublicManifest
        {
            Format = CipherVaultServiceConfiguration.InstallManifestFormat,
            FormatVersion =
                CipherVaultServiceConfiguration.InstallManifestFormatVersion,
            InstallId = installId,
            ProductVersion = typeof(StrictServiceHost).Assembly
                .GetName().Version?.ToString() ?? "1.0.0",
            ServiceName = StrictServiceHost.ServiceName,
            ServiceAccount = configuration.ServiceAccount,
            ServiceSid = serviceSid.Value,
            OwnerSid = configuration.OwnerSid,
            Pipes = FixedPipes(),
            AllowedServerImages =
            [
                ToManifestImage(
                    VerifiedProcessKind.HostService.ToString(),
                    Path.GetFullPath(imagePaths[0]),
                    images[0],
                    configuration.ProcessPolicy!.AllowedSignerThumbprints![0])
            ],
            AllowedClientImages = imagePaths
                .Skip(1)
                .Select((path, index) => ToManifestImage(
                    ImageKind(index + 1),
                    Path.GetFullPath(path),
                    images[index + 1],
                    configuration.ProcessPolicy!.AllowedSignerThumbprints![0]))
                .ToArray()
        };
    }

    private static CipherVaultImagePolicyFile ToManifestImage(
        string kind,
        string path,
        ServiceImageEvidence image,
        string signerThumbprint)
    {
        return new CipherVaultImagePolicyFile
        {
            Kind = kind,
            Path = path,
            Sha256 = image.ImageSha256,
            SignerThumbprint = signerThumbprint,
            SignerSubject = image.SignerSubject
        };
    }

    private static IEnumerable<string> EnumerateImagePaths(
        HostServiceInstallOptions options)
    {
        yield return options.HostExecutablePath;
        foreach (var path in new[]
                 {
                     options.UiExecutablePath,
                     options.CliExecutablePath,
                     options.BrokerExecutablePath,
                     options.McpExecutablePath
                 })
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumerateImagePathsFromConfiguration(
        CipherVaultServiceConfigFile configuration)
    {
        return configuration.ProcessPolicy?.AllowedImages?
            .Select(image => image.Path!)
            ?? [];
    }

    private static string ImageKind(int index)
    {
        return index switch
        {
            0 => VerifiedProcessKind.HostService.ToString(),
            1 => VerifiedProcessKind.AdminUi.ToString(),
            2 => VerifiedProcessKind.AdminCli.ToString(),
            3 => VerifiedProcessKind.AuthBroker.ToString(),
            4 => VerifiedProcessKind.McpBridge.ToString(),
            _ => $"client-{index}"
        };
    }

    private static CipherVaultPipeConfigFile FixedPipes()
    {
        return new CipherVaultPipeConfigFile
        {
            Admin = "CipherVault.Admin.v1",
            Broker = "CipherVault.Broker.v1",
            Bridge = "CipherVault.Bridge.v1",
            RejectRemoteClients = true
        };
    }

    private static void EnsureFreshInstall(CipherVaultServicePaths paths)
    {
        if ((Directory.Exists(paths.DataRoot) &&
             Directory.EnumerateFileSystemEntries(paths.DataRoot).Any()) ||
            File.Exists(paths.ServiceConfigPath) ||
            File.Exists(paths.PublicManifestPath) ||
            File.Exists(paths.ModePath) ||
            File.Exists(Path.Combine(paths.VaultPath, "vault.db")) ||
            File.Exists(Path.Combine(paths.VaultPath, "vault.keys")))
        {
            throw new VaultException(
                VaultErrorCode.AlreadyExists,
                "Strict installation requires an empty ProgramData Vault layout.");
        }
    }

    private static void WriteJson<T>(
        string path,
        T value,
        ICollection<string> writtenFiles)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Install file path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath);
            writtenFiles.Add(fullPath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void EnsureCommandSucceeded(
        WindowsServiceCommandResult result,
        string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new VaultException(
            VaultErrorCode.StorageError,
            string.IsNullOrWhiteSpace(detail)
                ? $"Unable to {operation} the Windows service."
                : $"Unable to {operation} the Windows service: {detail}");
    }

    private static SecurityIdentifier ParseSid(string value, string fieldName)
    {
        try
        {
            return new SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"{fieldName} is invalid.",
                exception);
        }
    }

    private static void ValidateSha256(string value, string fieldName)
    {
        if (value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"{fieldName} must contain 64 hex characters.");
        }
    }

    private static bool IsUnderPath(string path, string parent)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar);
        return normalizedPath.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RemoveEmptyInstallDirectories(
        CipherVaultServicePaths paths)
    {
        foreach (var directory in new[]
                 {
                     paths.TransferRoot,
                     paths.LogsPath,
                     paths.LocksPath,
                     paths.VaultPath,
                     paths.DataRoot
                 })
        {
            try
            {
                if (Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
