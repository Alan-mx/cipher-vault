using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Configuration;

public enum McpAccessMode
{
    ReadOnly,
    ReadWrite
}

public enum AgentAuthorizationMode
{
    AccessGrant,
    StaticToken
}

public sealed record CipherVaultSessionConfigFile
{
    public int? IdleMinutes { get; init; }
    public int? HardHours { get; init; }
}

public static class CipherVaultConfiguration
{
    private const long MaxConfigBytes = 256 * 1024;
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

    public static string DefaultConfigPath => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CipherVault",
        "config.json"));

    public static string DefaultVaultPath => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "CipherVault"));

    internal static string ReadBoundedText(
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

    internal static string ResolveVaultPath(
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

    internal static void ValidateNoSensitiveFields(JsonElement element)
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

    internal static McpAccessMode? ParseAccessMode(string? value)
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

    public static AgentAuthorizationMode? ParseAuthorizationMode(string? value)
    {
        var normalized = GetNonEmpty(value);
        if (normalized is null)
        {
            return null;
        }
        return normalized.ToLowerInvariant() switch
        {
            "accessgrant" => AgentAuthorizationMode.AccessGrant,
            "statictoken" => AgentAuthorizationMode.StaticToken,
            _ => throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Agent authorizationMode must be accessGrant or staticToken.")
        };
    }

    internal static int? ParseOptionalInteger(string? value, string fieldName)
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

    internal static string? GetNonEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    private static string NormalizeFieldName(string fieldName)
    {
        return new string(
            fieldName
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
    }
}
