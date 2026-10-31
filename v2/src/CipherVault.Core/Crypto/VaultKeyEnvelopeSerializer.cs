using System.Text.Json;
using System.Text.Json.Serialization;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Core.Crypto;

public static class VaultKeyEnvelopeSerializer
{
    public const int MaxEnvelopeBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = true
    };

    public static async Task<VaultKeyEnvelope> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length is <= 0 or > MaxEnvelopeBytes)
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Vault key envelope size is invalid.");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var envelope = await JsonSerializer.DeserializeAsync<VaultKeyEnvelope>(
                stream,
                JsonOptions,
                cancellationToken);
            if (envelope is null)
            {
                throw new VaultException(VaultErrorCode.CorruptVault, "Vault key envelope is empty.");
            }

            envelope.Validate();
            return envelope;
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Vault key envelope is invalid.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to read the Vault key envelope.");
        }
    }

    public static byte[] Serialize(VaultKeyEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.Validate();
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    public static Task WriteAtomicAsync(
        string path,
        VaultKeyEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        AtomicFileWriter.WriteAsync(path, Serialize(envelope), cancellationToken);
}
