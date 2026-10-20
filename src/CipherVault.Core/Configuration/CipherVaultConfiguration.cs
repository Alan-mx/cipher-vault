using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;

namespace CipherVault.Core.Configuration;

public enum McpAccessMode
{
    ReadOnly,
    ReadWrite
}

public sealed record CipherVaultConfigOverrides
{
    public static CipherVaultConfigOverrides Empty { get; } = new();

    public string? VaultPath { get; init; }
    public int? IdleMinutes { get; init; }
    public int? HardHours { get; init; }
    public McpAccessMode? AccessMode { get; init; }
}

public sealed record ResolvedCipherVaultConfig(
    string? VaultPath,
    VaultSessionOptions SessionOptions,
    McpAccessMode McpAccessMode,
    string ConfigPath);

public sealed record CipherVaultConfigFile
{
    public int ConfigVersion { get; init; }
    public string? VaultPath { get; init; }
    public CipherVaultSessionConfigFile? Session { get; init; }
    public CipherVaultMcpConfigFile? Mcp { get; init; }
}

public sealed record CipherVaultSessionConfigFile
{
    public int? IdleMinutes { get; init; }
    public int? HardHours { get; init; }
}

public sealed record CipherVaultMcpConfigFile
{
    public string? AccessMode { get; init; }
}

public static class CipherVaultConfiguration
{
    private const int CurrentConfigVersion = 1;
    private const long MaxConfigBytes = 256 * 1024;
    private const int DefaultIdleMinutes = 15;
    private const int DefaultHardHours = 4;
    private static readonly HashSet<string> SensitiveFieldNames =
    [
        "password",
        "recoverykey",
        "token",
        "accesstoken",
        "secret",
        "credential",
        "credentials"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultConfigPath => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CipherVault",
        "config.json"));

    public static string DefaultVaultPath => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "CipherVault"));

    public static ResolvedCipherVaultConfig Load(
        CipherVaultConfigOverrides commandLineOverrides,
        string? commandLineConfigPath = null,
        Func<string, string?>? getEnvironmentVariable = null,
        CancellationToken cancellationToken = default)
    {
        return LoadCore(
            commandLineOverrides,
            commandLineConfigPath,
            getEnvironmentVariable,
            DefaultConfigPath,
            DefaultVaultPath,
            cancellationToken);
    }

    internal static ResolvedCipherVaultConfig LoadCore(
        CipherVaultConfigOverrides commandLineOverrides,
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
            var environmentConfigPath = GetNonEmpty(
                readEnvironment("CIPHER_VAULT_CONFIG"));
            var explicitConfigPath = GetNonEmpty(commandLineConfigPath)
                ?? environmentConfigPath;
            var configPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    explicitConfigPath ?? defaultConfigPath));
            var configFile = ReadConfigFile(
                configPath,
                isExplicit: explicitConfigPath is not null,
                cancellationToken);
            var environmentOverrides = ReadEnvironmentOverrides(
                readEnvironment,
                commandLineOverrides);

            var configDirectory = Path.GetDirectoryName(configPath)
                ?? Environment.CurrentDirectory;
            var vaultPath = ResolveVaultPath(
                commandLineOverrides.VaultPath,
                environmentOverrides.VaultPath,
                configFile?.VaultPath,
                configDirectory,
                defaultVaultPath);
            var idleMinutes = commandLineOverrides.IdleMinutes
                ?? environmentOverrides.IdleMinutes
                ?? configFile?.Session?.IdleMinutes
                ?? DefaultIdleMinutes;
            var hardHours = commandLineOverrides.HardHours
                ?? environmentOverrides.HardHours
                ?? configFile?.Session?.HardHours
                ?? DefaultHardHours;
            var accessMode = commandLineOverrides.AccessMode
                ?? environmentOverrides.AccessMode
                ?? ParseAccessMode(configFile?.Mcp?.AccessMode)
                ?? McpAccessMode.ReadWrite;
            var sessionOptions = VaultSessionOptions.FromMinutes(idleMinutes, hardHours);
            sessionOptions.Validate();

            return new ResolvedCipherVaultConfig(
                vaultPath,
                sessionOptions,
                accessMode,
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
                $"CipherVault configuration is invalid: {exception.Message}",
                exception);
        }
    }

    private static CipherVaultConfigFile? ReadConfigFile(
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
                    $"CipherVault configuration file was not found: {configPath}");
            }
            return null;
        }

        var json = ReadBoundedText(configPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "CipherVault configuration file is empty.");
        }

        using (var document = JsonDocument.Parse(json))
        {
            ValidateNoSensitiveFields(document.RootElement);
        }

        var configFile = JsonSerializer.Deserialize<CipherVaultConfigFile>(
            json,
            JsonOptions) ?? throw new VaultException(
                VaultErrorCode.InvalidInput,
                "CipherVault configuration file is empty.");
        if (configFile.ConfigVersion != CurrentConfigVersion)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"CipherVault configVersion must be {CurrentConfigVersion}.");
        }
        return configFile;
    }

    private static string ReadBoundedText(
        string configPath,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > MaxConfigBytes)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"CipherVault configuration exceeds {MaxConfigBytes} bytes.");
        }

        using var buffer = new MemoryStream((int)stream.Length);
        var chunk = new byte[4096];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > MaxConfigBytes)
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"CipherVault configuration exceeds {MaxConfigBytes} bytes.");
            }
            buffer.Write(chunk, 0, read);
        }

        var text = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(buffer.ToArray());
        return text.Length > 0 && text[0] == '\uFEFF'
            ? text[1..]
            : text;
    }

    private static CipherVaultConfigOverrides ReadEnvironmentOverrides(
        Func<string, string?> readEnvironment,
        CipherVaultConfigOverrides commandLineOverrides)
    {
        McpAccessMode? accessMode = null;
        if (commandLineOverrides.AccessMode is null)
        {
            accessMode = ParseAccessMode(
                GetNonEmpty(readEnvironment("CIPHER_VAULT_ACCESS_MODE")));
            var readOnly = ParseOptionalBoolean(
                GetNonEmpty(readEnvironment("CIPHER_VAULT_READ_ONLY")),
                "CIPHER_VAULT_READ_ONLY");
            if (readOnly is not null)
            {
                var requestedMode = readOnly.Value
                    ? McpAccessMode.ReadOnly
                    : McpAccessMode.ReadWrite;
                if (accessMode is not null && accessMode != requestedMode)
                {
                    throw new VaultException(
                        VaultErrorCode.InvalidInput,
                        "CIPHER_VAULT_READ_ONLY conflicts with CIPHER_VAULT_ACCESS_MODE.");
                }
                accessMode = requestedMode;
            }
        }

        return new CipherVaultConfigOverrides
        {
            VaultPath = GetNonEmpty(readEnvironment("CIPHER_VAULT_PATH")),
            IdleMinutes = commandLineOverrides.IdleMinutes is null
                ? ParseOptionalInteger(
                    GetNonEmpty(readEnvironment("CIPHER_VAULT_IDLE_MINUTES")),
                    "CIPHER_VAULT_IDLE_MINUTES")
                : null,
            HardHours = commandLineOverrides.HardHours is null
                ? ParseOptionalInteger(
                    GetNonEmpty(readEnvironment("CIPHER_VAULT_HARD_HOURS")),
                    "CIPHER_VAULT_HARD_HOURS")
                : null,
            AccessMode = accessMode
        };
    }

    private static string ResolveVaultPath(
        string? commandLineVaultPath,
        string? environmentVaultPath,
        string? configVaultPath,
        string configDirectory,
        string defaultVaultPath)
    {
        var commandLine = GetNonEmpty(commandLineVaultPath);
        if (commandLine is not null)
        {
            return ResolvePathFromCurrentDirectory(commandLine);
        }
        var environment = GetNonEmpty(environmentVaultPath);
        if (environment is not null)
        {
            return ResolvePathFromCurrentDirectory(environment);
        }
        var configured = GetNonEmpty(configVaultPath);
        if (configured is not null)
        {
            return ResolvePathFromDirectory(configured, configDirectory);
        }
        return Path.GetFullPath(defaultVaultPath);
    }

    private static string ResolvePathFromCurrentDirectory(string path)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    private static string ResolvePathFromDirectory(string path, string directory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return Path.GetFullPath(
            Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(directory, expanded));
    }

    private static void ValidateNoSensitiveFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (SensitiveFieldNames.Contains(NormalizeFieldName(property.Name)))
                {
                    throw new VaultException(
                        VaultErrorCode.InvalidInput,
                        $"CipherVault configuration must not contain the '{property.Name}' field.");
                }
                ValidateNoSensitiveFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoSensitiveFields(item);
            }
        }
    }

    private static string NormalizeFieldName(string fieldName)
    {
        return new string(
            fieldName
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
    }

    private static McpAccessMode? ParseAccessMode(string? value)
    {
        var normalized = GetNonEmpty(value);
        if (normalized is null)
        {
            return null;
        }
        return normalized.ToLowerInvariant() switch
        {
            "readonly" => McpAccessMode.ReadOnly,
            "readwrite" => McpAccessMode.ReadWrite,
            _ => throw new VaultException(
                VaultErrorCode.InvalidInput,
                "MCP accessMode must be readOnly or readWrite.")
        };
    }

    private static int? ParseOptionalInteger(string? value, string fieldName)
    {
        if (value is null)
        {
            return null;
        }
        if (!int.TryParse(value, out var result))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"{fieldName} must be an integer.");
        }
        return result;
    }

    private static bool? ParseOptionalBoolean(string? value, string fieldName)
    {
        if (value is null)
        {
            return null;
        }
        return value.ToLowerInvariant() switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"{fieldName} must be 0 or 1.")
        };
    }

    private static string? GetNonEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
