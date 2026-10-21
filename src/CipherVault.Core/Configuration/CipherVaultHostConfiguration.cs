using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;

namespace CipherVault.Core.Configuration;

public sealed record CipherVaultHostConfigOverrides
{
    public static CipherVaultHostConfigOverrides Empty { get; } = new();

    public string? VaultPath { get; init; }
    public int? IdleMinutes { get; init; }
    public int? HardHours { get; init; }
}

public sealed record CipherVaultHostConfigFile
{
    public int ConfigVersion { get; init; }
    public string? VaultPath { get; init; }
    public CipherVaultSessionConfigFile? Session { get; init; }
    public CipherVaultHostEndpointConfigFile? Host { get; init; }
    public IReadOnlyList<CipherVaultClientConfigFile>? Clients { get; init; }
}

public sealed record CipherVaultHostEndpointConfigFile
{
    public string? BindAddress { get; init; }
    public int? Port { get; init; }
    public string? McpPath { get; init; }
    public string? AdminPath { get; init; }
    public long? MaxRequestBodyBytes { get; init; }
    public IReadOnlyList<string>? AllowedOrigins { get; init; }
}

public sealed record CipherVaultClientConfigFile
{
    public string? Id { get; init; }
    public string? TokenHash { get; init; }
    public string? AccessMode { get; init; }
    public string? AuthorizationMode { get; init; }
    public bool? Enabled { get; init; }
}

public sealed record ResolvedCipherVaultHostConfig(
    string VaultPath,
    VaultSessionOptions SessionOptions,
    HostEndpointOptions Endpoint,
    IReadOnlyList<AgentClientPolicy> Clients,
    string ConfigPath);

public sealed record HostEndpointOptions(
    string BindAddress,
    int Port,
    string McpPath,
    string AdminPath,
    long MaxRequestBodyBytes,
    IReadOnlySet<string> AllowedOrigins);

public sealed record AgentClientPolicy(
    string ClientId,
    byte[] TokenHash,
    McpAccessMode AccessMode,
    AgentAuthorizationMode AuthorizationMode,
    bool Enabled);

public static class CipherVaultHostConfiguration
{
    public const int CurrentConfigVersion = 2;
    private const int DefaultIdleMinutes = 15;
    private const int DefaultHardHours = 4;
    private const int DefaultPort = 43127;
    private const long DefaultMaxRequestBodyBytes = 4 * 1024 * 1024;
    private const long MinimumRequestBodyBytes = 1024;
    private const long MaximumRequestBodyBytes = 16 * 1024 * 1024;
    private const int MaximumOrigins = 16;
    public const int MaximumClients = 64;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ResolvedCipherVaultHostConfig Load(
        CipherVaultHostConfigOverrides commandLineOverrides,
        string? commandLineConfigPath = null,
        Func<string, string?>? getEnvironmentVariable = null,
        CancellationToken cancellationToken = default)
    {
        return LoadCore(
            commandLineOverrides,
            commandLineConfigPath,
            getEnvironmentVariable,
            CipherVaultConfiguration.DefaultConfigPath,
            CipherVaultConfiguration.DefaultVaultPath,
            cancellationToken);
    }

    internal static ResolvedCipherVaultHostConfig LoadCore(
        CipherVaultHostConfigOverrides commandLineOverrides,
        string? commandLineConfigPath,
        Func<string, string?>? getEnvironmentVariable,
        string defaultConfigPath,
        string defaultVaultPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readEnvironment = getEnvironmentVariable
            ?? Environment.GetEnvironmentVariable;

        try
        {
            var environmentConfigPath = CipherVaultConfiguration.GetNonEmpty(
                readEnvironment("CIPHER_VAULT_CONFIG"));
            var explicitConfigPath = CipherVaultConfiguration.GetNonEmpty(commandLineConfigPath)
                ?? environmentConfigPath;
            var configPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    explicitConfigPath ?? defaultConfigPath));
            var configFile = ReadConfigFile(
                configPath,
                isExplicit: explicitConfigPath is not null,
                cancellationToken);
            var environmentVaultPath = CipherVaultConfiguration.GetNonEmpty(
                readEnvironment("CIPHER_VAULT_PATH"));
            var environmentIdleMinutes =
                commandLineOverrides.IdleMinutes is null
                    ? CipherVaultConfiguration.ParseOptionalInteger(
                        CipherVaultConfiguration.GetNonEmpty(
                            readEnvironment("CIPHER_VAULT_IDLE_MINUTES")),
                        "CIPHER_VAULT_IDLE_MINUTES")
                    : null;
            var environmentHardHours =
                commandLineOverrides.HardHours is null
                    ? CipherVaultConfiguration.ParseOptionalInteger(
                        CipherVaultConfiguration.GetNonEmpty(
                            readEnvironment("CIPHER_VAULT_HARD_HOURS")),
                        "CIPHER_VAULT_HARD_HOURS")
                    : null;
            var configDirectory = Path.GetDirectoryName(configPath)
                ?? Environment.CurrentDirectory;
            var vaultPath = CipherVaultConfiguration.ResolveVaultPath(
                commandLineOverrides.VaultPath,
                environmentVaultPath,
                configFile?.VaultPath,
                configDirectory,
                defaultVaultPath);
            var idleMinutes = commandLineOverrides.IdleMinutes
                ?? environmentIdleMinutes
                ?? configFile?.Session?.IdleMinutes
                ?? DefaultIdleMinutes;
            var hardHours = commandLineOverrides.HardHours
                ?? environmentHardHours
                ?? configFile?.Session?.HardHours
                ?? DefaultHardHours;
            var sessionOptions = VaultSessionOptions.FromMinutes(idleMinutes, hardHours);
            sessionOptions.Validate();
            var endpoint = ResolveEndpoint(configFile?.Host);
            var clients = ResolveClients(configFile?.Clients);

            return new ResolvedCipherVaultHostConfig(
                vaultPath,
                sessionOptions,
                endpoint,
                clients,
                configPath);
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            FormatException or
            DecoderFallbackException or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"CipherVault Host configuration is invalid: {exception.Message}",
                exception);
        }
    }

    private static CipherVaultHostConfigFile? ReadConfigFile(
        string configPath,
        bool isExplicit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(configPath))
        {
            if (isExplicit)
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"CipherVault Host configuration file was not found: {configPath}");
            }
            return null;
        }

        var json = CipherVaultConfiguration.ReadBoundedText(configPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "CipherVault Host configuration file is empty.");
        }

        using (var document = JsonDocument.Parse(json))
        {
            CipherVaultConfiguration.ValidateNoSensitiveFields(document.RootElement);
            RejectRemovedClientFields(document.RootElement);
        }

        var configFile = JsonSerializer.Deserialize<CipherVaultHostConfigFile>(
            json,
            JsonOptions) ?? throw new VaultException(
                VaultErrorCode.InvalidInput,
                "CipherVault Host configuration file is empty.");
        if (configFile.ConfigVersion != CurrentConfigVersion)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"CipherVault Host configVersion must be {CurrentConfigVersion}.");
        }

        return configFile;
    }

    private static HostEndpointOptions ResolveEndpoint(
        CipherVaultHostEndpointConfigFile? endpoint)
    {
        var bindAddress = CipherVaultConfiguration.GetNonEmpty(endpoint?.BindAddress)
            ?? "127.0.0.1";
        if (!string.Equals(bindAddress, "127.0.0.1", StringComparison.Ordinal))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "host.bindAddress must be 127.0.0.1.");
        }

        var port = endpoint?.Port ?? DefaultPort;
        if (port is < 1 or > 65535)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "host.port must be between 1 and 65535.");
        }

        var mcpPath = NormalizeEndpointPath(endpoint?.McpPath, "/mcp", "host.mcpPath");
        var adminPath = NormalizeEndpointPath(
            endpoint?.AdminPath,
            "/admin/v1",
            "host.adminPath");
        if (string.Equals(mcpPath, adminPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "host.mcpPath and host.adminPath must be different.");
        }
        if (string.Equals(mcpPath, "/", StringComparison.Ordinal) ||
            string.Equals(adminPath, "/", StringComparison.Ordinal) ||
            IsPathUnder(mcpPath, adminPath) ||
            IsPathUnder(adminPath, mcpPath))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "host.mcpPath and host.adminPath must be non-root, non-overlapping paths.");
        }

        var maxRequestBodyBytes = endpoint?.MaxRequestBodyBytes
            ?? DefaultMaxRequestBodyBytes;
        if (maxRequestBodyBytes is < MinimumRequestBodyBytes or > MaximumRequestBodyBytes)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"host.maxRequestBodyBytes must be between {MinimumRequestBodyBytes} and {MaximumRequestBodyBytes}.");
        }

        return new HostEndpointOptions(
            bindAddress,
            port,
            mcpPath,
            adminPath,
            maxRequestBodyBytes,
            NormalizeOrigins(endpoint?.AllowedOrigins));
    }

    private static IReadOnlyList<AgentClientPolicy> ResolveClients(
        IReadOnlyList<CipherVaultClientConfigFile>? clients)
    {
        if (clients is null || clients.Count == 0)
        {
            return [];
        }
        if (clients.Count > MaximumClients)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"clients must contain no more than {MaximumClients} entries.");
        }

        var result = new List<AgentClientPolicy>(clients.Count);
        var clientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < clients.Count; index++)
        {
            var client = clients[index];
            var clientId = CipherVaultConfiguration.GetNonEmpty(client.Id)
                ?? throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"clients[{index}].id is required.");
            if (!IsValidClientId(clientId))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"clients[{index}].id is invalid.");
            }
            if (!clientIds.Add(clientId))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"Duplicate client id: {clientId}.");
            }

            var tokenHash = ParseTokenHash(client.TokenHash, index);
            var accessMode = CipherVaultConfiguration.ParseAccessMode(client.AccessMode)
                ?? McpAccessMode.ReadOnly;
            var authorizationMode =
                CipherVaultConfiguration.ParseAuthorizationMode(client.AuthorizationMode)
                ?? AgentAuthorizationMode.AccessGrant;
            if (authorizationMode == AgentAuthorizationMode.StaticToken &&
                accessMode != McpAccessMode.ReadOnly)
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"clients[{index}] staticToken only supports readOnly accessMode.");
            }
            result.Add(new AgentClientPolicy(
                clientId,
                tokenHash,
                accessMode,
                authorizationMode,
                client.Enabled ?? true));
        }

        return result;
    }

    private static byte[] ParseTokenHash(string? value, int index)
    {
        var normalized = CipherVaultConfiguration.GetNonEmpty(value);
        if (normalized is null ||
            !normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"clients[{index}].tokenHash must use sha256:<hex> format.");
        }

        var hashText = normalized["sha256:".Length..];
        if (hashText.Length != 64 ||
            hashText.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"clients[{index}].tokenHash must contain a 64-character SHA-256 value.");
        }

        return Convert.FromHexString(hashText);
    }

    private static void RejectRemovedClientFields(JsonElement root)
    {
        if (!root.TryGetProperty("clients", out var clients) ||
            clients.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var client in clients.EnumerateArray())
        {
            if (client.ValueKind == JsonValueKind.Object &&
                client.TryGetProperty("allowLock", out _))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"clients[{index}].allowLock is no longer supported.");
            }
            index++;
        }
    }

    private static IReadOnlySet<string> NormalizeOrigins(
        IReadOnlyList<string>? origins)
    {
        if (origins is null || origins.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        if (origins.Count > MaximumOrigins)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"host.allowedOrigins must contain no more than {MaximumOrigins} entries.");
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in origins)
        {
            var normalized = CipherVaultConfiguration.GetNonEmpty(origin)
                ?? throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "host.allowedOrigins cannot contain empty values.");
            if (string.Equals(normalized, "*", StringComparison.Ordinal) ||
                !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                uri.AbsolutePath is not ("" or "/"))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"Invalid allowed origin: {normalized}");
            }
            var canonical = uri.GetLeftPart(UriPartial.Authority);
            if (!result.Add(canonical))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"Duplicate allowed origin: {canonical}");
            }
        }

        return result;
    }

    private static string NormalizeEndpointPath(
        string? value,
        string defaultValue,
        string fieldName)
    {
        var path = CipherVaultConfiguration.GetNonEmpty(value) ?? defaultValue;
        if (!path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('?') ||
            path.Contains('#') ||
            path.Any(character =>
                char.IsWhiteSpace(character) ||
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '/' or '-' or '_' or '.')))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"{fieldName} must be a safe absolute path without query or fragment.");
        }
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static bool IsValidClientId(string clientId)
    {
        return clientId.Length is >= 1 and <= 64 &&
            clientId.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-');
    }

    private static bool IsPathUnder(string path, string parent)
    {
        return path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    }
}
