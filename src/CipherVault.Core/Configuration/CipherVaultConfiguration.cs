using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Configuration;

public enum McpAccessMode
{
    ReadOnly,
    ReadWrite
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

    private static string NormalizeFieldName(string fieldName)
    {
        return new string(
            fieldName
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
    }
}
