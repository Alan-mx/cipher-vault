using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Configuration;

public enum HostSecurityMode
{
    Strict,
    Compatibility
}

public sealed record CipherVaultServiceConfigFile
{
    public int ConfigVersion { get; init; }
    public string? SecurityMode { get; init; }
    public string? OwnerSid { get; init; }
    public string? ServiceAccount { get; init; }
    public string? VaultPath { get; init; }
    public string? DataRoot { get; init; }
    public CipherVaultSessionConfigFile? Session { get; init; }
    public CipherVaultServiceHostConfigFile? Host { get; init; }
    public CipherVaultPipeConfigFile? Pipes { get; init; }
    public CipherVaultProcessPolicyConfigFile? ProcessPolicy { get; init; }
    public CipherVaultAgentRuntimeConfigFile? Agent { get; init; }
}

public sealed record CipherVaultServiceHostConfigFile
{
    public string? McpPath { get; init; }
    public long? MaxRequestBodyBytes { get; init; }
    public IReadOnlyList<string>? AllowedOrigins { get; init; }
}

public sealed record CipherVaultPipeConfigFile
{
    public string? Admin { get; init; }
    public string? Broker { get; init; }
    public string? Bridge { get; init; }
    public bool? RejectRemoteClients { get; init; }
}

public sealed record CipherVaultProcessPolicyConfigFile
{
    public IReadOnlyList<string>? AllowedSignerThumbprints { get; init; }
    public IReadOnlyList<CipherVaultImagePolicyFile>? AllowedImages { get; init; }
    public bool? RequireSha256 { get; init; }
}

public sealed record CipherVaultImagePolicyFile
{
    public string? Kind { get; init; }
    public string? Path { get; init; }
    public string? Sha256 { get; init; }
    public string? SignerThumbprint { get; init; }
    public string? SignerSubject { get; init; }
}

public sealed record CipherVaultAgentRuntimeConfigFile
{
    public string? StrictTransport { get; init; }
    public bool? CompatibilityHttpEnabled { get; init; }
    public IReadOnlyList<CipherVaultStrictBridgeConfigFile>? Bridges { get; init; }
    public IReadOnlyList<CipherVaultClientConfigFile>? Clients { get; init; }
}

public sealed record CipherVaultStrictBridgeConfigFile
{
    public string? Id { get; init; }
    public string? AccessMode { get; init; }
    public CipherVaultImagePolicyFile? Image { get; init; }
    public bool? Enabled { get; init; }
}

public sealed record CipherVaultInstallPublicManifest
{
    public string? Format { get; init; }
    public int FormatVersion { get; init; }
    public Guid InstallId { get; init; }
    public string? ProductVersion { get; init; }
    public string? ServiceName { get; init; }
    public string? ServiceAccount { get; init; }
    public string? ServiceSid { get; init; }
    public string? OwnerSid { get; init; }
    public CipherVaultPipeConfigFile? Pipes { get; init; }
    public IReadOnlyList<CipherVaultImagePolicyFile>? AllowedServerImages { get; init; }
    public IReadOnlyList<CipherVaultImagePolicyFile>? AllowedClientImages { get; init; }
}

public sealed record CipherVaultVaultModeManifest
{
    public string? Format { get; init; }
    public int FormatVersion { get; init; }
    public string? Mode { get; init; }
    public string? OwnerSid { get; init; }
    public Guid InstallId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record CipherVaultClientBootstrapConfigFile
{
    public int ConfigVersion { get; init; }
    public string? SecurityMode { get; init; }
    public string? InstallManifestPath { get; init; }
}

public sealed record CipherVaultServicePaths(
    string InstallRoot,
    string DataRoot,
    string PublicManifestPath,
    string ServiceConfigPath,
    string VaultPath,
    string ModePath,
    string LogsPath,
    string LocksPath,
    string TransferRoot,
    string ClientConfigPath)
{
    public string HostInstanceLockPath =>
        Path.Combine(LocksPath, "host.instance.lock");

    public string VaultOwnerLockPath =>
        Path.Combine(LocksPath, "vault.owner.lock");

    public static CipherVaultServicePaths Default => Create(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static CipherVaultServicePaths Create(
        string installRoot,
        string dataRoot,
        string localAppDataRoot)
    {
        var appInstallRoot = Path.Combine(installRoot, "CipherVault");
        var appDataRoot = Path.Combine(dataRoot, "CipherVault");
        var vaultPath = Path.Combine(appDataRoot, "Vault");
        return new CipherVaultServicePaths(
            Path.GetFullPath(appInstallRoot),
            Path.GetFullPath(appDataRoot),
            Path.Combine(appDataRoot, "install-public.json"),
            Path.Combine(appDataRoot, "service.json"),
            vaultPath,
            Path.Combine(vaultPath, "vault.mode"),
            Path.Combine(appDataRoot, "Logs"),
            Path.Combine(appDataRoot, "locks"),
            Path.Combine(appDataRoot, "Transfer"),
            Path.Combine(localAppDataRoot, "CipherVault", "client.json"));
    }
}

public static class CipherVaultServiceConfiguration
{
    public const int CurrentConfigVersion = 3;
    public const string InstallManifestFormat = "cipher-vault-install";
    public const int InstallManifestFormatVersion = 1;
    public const string VaultModeFormat = "cipher-vault-mode";
    public const int VaultModeFormatVersion = 1;
    public const string StrictModeValue = "strict";
    public const string CompatibilityModeValue = "compatibility";
    private const long MaxConfigBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static CipherVaultServiceConfigFile Load(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(configPath);
            if (!File.Exists(fullPath))
            {
                throw new VaultException(
                    VaultErrorCode.NotFound,
                    $"Service configuration was not found: {fullPath}");
            }
            var json = CipherVaultConfiguration.ReadBoundedText(fullPath, cancellationToken);
            return Parse(json);
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Service configuration could not be read.",
                exception);
        }
    }

    public static CipherVaultInstallPublicManifest LoadInstallManifest(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(manifestPath);
            if (!File.Exists(fullPath))
            {
                throw new VaultException(
                    VaultErrorCode.NotFound,
                    $"Install manifest was not found: {fullPath}");
            }
            var json = CipherVaultConfiguration.ReadBoundedText(
                fullPath,
                cancellationToken);
            return ParseInstallManifest(json);
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Install manifest could not be read.",
                exception);
        }
    }

    public static CipherVaultServiceConfigFile Parse(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Service configuration is empty.");
            }
            using (var document = JsonDocument.Parse(json))
            {
                CipherVaultConfiguration.ValidateNoSensitiveFields(document.RootElement);
            }
            var config = JsonSerializer.Deserialize<CipherVaultServiceConfigFile>(
                json,
                JsonOptions) ?? throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Service configuration is empty.");
            Validate(config);
            return config;
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Service configuration is invalid: {exception.Message}",
                exception);
        }
    }

    public static CipherVaultInstallPublicManifest ParseInstallManifest(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw Invalid("Install manifest is empty.");
            }
            using (var document = JsonDocument.Parse(json))
            {
                CipherVaultConfiguration.ValidateNoSensitiveFields(document.RootElement);
            }
            var manifest = JsonSerializer.Deserialize<CipherVaultInstallPublicManifest>(
                json,
                JsonOptions) ?? throw Invalid("Install manifest is empty.");
            Validate(manifest);
            return manifest;
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Install manifest is invalid: {exception.Message}",
                exception);
        }
    }

    public static void Validate(CipherVaultServiceConfigFile config)
    {
        if (config.ConfigVersion != CurrentConfigVersion)
        {
            throw Invalid(
                $"Service configVersion must be {CurrentConfigVersion}.");
        }
        var mode = ParseSecurityMode(config.SecurityMode);
        var dataRoot = RequireAbsolutePath(config.DataRoot, "dataRoot");
        var vaultPath = RequireAbsolutePath(config.VaultPath, "vaultPath");
        if (!IsUnderPath(vaultPath, dataRoot))
        {
            throw Invalid("vaultPath must be inside dataRoot in service mode.");
        }
        ValidatePipes(config.Pipes);
        if (mode == HostSecurityMode.Strict)
        {
            RequireNonEmpty(config.OwnerSid, "ownerSid");
            RequireNonEmpty(config.ServiceAccount, "serviceAccount");
            ValidateStrictProcessPolicy(config.ProcessPolicy);
            ValidateStrictAgent(config.Agent);
        }
    }

    public static void Validate(CipherVaultInstallPublicManifest manifest)
    {
        if (!string.Equals(
                manifest.Format,
                InstallManifestFormat,
                StringComparison.Ordinal) ||
            manifest.FormatVersion != InstallManifestFormatVersion ||
            manifest.InstallId == Guid.Empty)
        {
            throw Invalid("Install public manifest format is invalid.");
        }
        RequireNonEmpty(manifest.ProductVersion, "productVersion");
        RequireNonEmpty(manifest.ServiceName, "serviceName");
        RequireNonEmpty(manifest.ServiceAccount, "serviceAccount");
        RequireNonEmpty(manifest.ServiceSid, "serviceSid");
        RequireNonEmpty(manifest.OwnerSid, "ownerSid");
        ValidatePipes(manifest.Pipes);
        if (manifest.AllowedServerImages is null ||
            manifest.AllowedServerImages.Count == 0)
        {
            throw Invalid("allowedServerImages is required.");
        }
        foreach (var image in manifest.AllowedServerImages)
        {
            ValidateImage(image, "allowedServerImages");
        }
        if (manifest.AllowedClientImages is not null)
        {
            foreach (var image in manifest.AllowedClientImages)
            {
                ValidateImage(image, "allowedClientImages");
            }
        }
    }

    public static void Validate(CipherVaultVaultModeManifest manifest)
    {
        if (!string.Equals(
                manifest.Format,
                VaultModeFormat,
                StringComparison.Ordinal) ||
            manifest.FormatVersion != VaultModeFormatVersion ||
            manifest.InstallId == Guid.Empty ||
            manifest.CreatedAt == default)
        {
            throw Invalid("Vault mode manifest format is invalid.");
        }
        ParseSecurityMode(manifest.Mode);
        RequireNonEmpty(manifest.OwnerSid, "ownerSid");
    }

    public static HostSecurityMode ParseSecurityMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            StrictModeValue => HostSecurityMode.Strict,
            CompatibilityModeValue => HostSecurityMode.Compatibility,
            _ => throw Invalid("securityMode must be strict or compatibility.")
        };
    }

    private static void ValidatePipes(CipherVaultPipeConfigFile? pipes)
    {
        if (pipes is null)
        {
            throw Invalid("pipes is required.");
        }
        var admin = ValidatePipeName(pipes.Admin, "pipes.admin");
        var broker = ValidatePipeName(pipes.Broker, "pipes.broker");
        var bridge = ValidatePipeName(pipes.Bridge, "pipes.bridge");
        if (new[] { admin, broker, bridge }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
        {
            throw Invalid("Admin, Broker and Bridge pipe names must be distinct.");
        }
        if (pipes.RejectRemoteClients != true)
        {
            throw Invalid("pipes.rejectRemoteClients must be true.");
        }
    }

    private static void ValidateStrictProcessPolicy(
        CipherVaultProcessPolicyConfigFile? policy)
    {
        if (policy is null ||
            policy.AllowedSignerThumbprints is null ||
            policy.AllowedSignerThumbprints.Count == 0)
        {
            throw Invalid("processPolicy.allowedSignerThumbprints is required in strict mode.");
        }
        foreach (var thumbprint in policy.AllowedSignerThumbprints)
        {
            ValidateThumbprint(thumbprint, "processPolicy.allowedSignerThumbprints");
        }
        if (policy.RequireSha256 != true)
        {
            throw Invalid("processPolicy.requireSha256 must be true in strict mode.");
        }
        if (policy.AllowedImages is not null)
        {
            foreach (var image in policy.AllowedImages)
            {
                ValidateImage(image, "processPolicy.allowedImages");
            }
        }
    }

    private static void ValidateStrictAgent(CipherVaultAgentRuntimeConfigFile? agent)
    {
        if (agent is null ||
            !string.Equals(
                agent.StrictTransport,
                "stdio-bridge",
                StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("agent.strictTransport must be stdio-bridge in strict mode.");
        }
        if (agent.CompatibilityHttpEnabled == true)
        {
            throw Invalid("agent.compatibilityHttpEnabled must be false in strict mode.");
        }
        if (agent.Clients is { Count: > 0 })
        {
            throw Invalid("agent.clients is compatibility-only.");
        }
        if (agent.Bridges is not null)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bridge in agent.Bridges)
            {
                var id = RequireNonEmpty(bridge.Id, "agent.bridges[].id");
                if (!ids.Add(id))
                {
                    throw Invalid($"Duplicate strict bridge id: {id}.");
                }
                _ = CipherVaultConfiguration.ParseAccessMode(bridge.AccessMode)
                    ?? throw Invalid("Strict bridge accessMode is required.");
                ValidateImage(bridge.Image, "agent.bridges[].image");
            }
        }
    }

    private static void ValidateImage(
        CipherVaultImagePolicyFile? image,
        string fieldName)
    {
        if (image is null)
        {
            throw Invalid($"{fieldName} image is required.");
        }
        RequireNonEmpty(image.Kind, $"{fieldName}.kind");
        _ = RequireAbsolutePath(image.Path, $"{fieldName}.path");
        ValidateSha256(image.Sha256, $"{fieldName}.sha256");
        ValidateThumbprint(image.SignerThumbprint, $"{fieldName}.signerThumbprint");
    }

    private static string ValidatePipeName(string? value, string fieldName)
    {
        var name = RequireNonEmpty(value, fieldName);
        if (name.Length > 128 ||
            name.Contains('\\') ||
            name.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '_' or '-')))
        {
            throw Invalid($"{fieldName} is invalid.");
        }
        return name;
    }

    private static void ValidateSha256(string? value, string fieldName)
    {
        var hash = RequireNonEmpty(value, fieldName);
        if (hash.Length != 64 ||
            hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw Invalid($"{fieldName} must contain 64 hex characters.");
        }
    }

    private static void ValidateThumbprint(string? value, string fieldName)
    {
        var thumbprint = RequireNonEmpty(value, fieldName);
        if (thumbprint.Length != 64 ||
            thumbprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw Invalid($"{fieldName} must contain 64 hex characters.");
        }
    }

    private static string RequireAbsolutePath(string? value, string fieldName)
    {
        var path = RequireNonEmpty(value, fieldName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw Invalid($"{fieldName} must be an absolute path.");
        }
        return Path.GetFullPath(path);
    }

    private static string RequireNonEmpty(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw Invalid($"{fieldName} is required.")
            : value.Trim();
    }

    private static bool IsUnderPath(string path, string parent)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return normalizedPath.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static VaultException Invalid(string message)
    {
        return new VaultException(VaultErrorCode.InvalidInput, message);
    }
}
