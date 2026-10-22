using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Configuration;

public static class VaultModeStore
{
    private const int MaximumModeBytes = 64 * 1024;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions WriteOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    public static CipherVaultVaultModeManifest Read(
        string modePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(modePath);
            if (!File.Exists(fullPath))
            {
                throw new VaultException(
                    VaultErrorCode.NotFound,
                    $"Vault mode marker was not found: {fullPath}");
            }
            var json = ReadBoundedText(fullPath, cancellationToken);
            using (var document = JsonDocument.Parse(json))
            {
                CipherVaultConfiguration.ValidateNoSensitiveFields(
                    document.RootElement);
            }
            var manifest = JsonSerializer.Deserialize<CipherVaultVaultModeManifest>(
                json,
                ReadOptions) ?? throw Invalid("Vault mode marker is empty.");
            CipherVaultServiceConfiguration.Validate(manifest);
            return manifest;
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            ArgumentException or
            NotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Vault mode marker is invalid: {exception.Message}",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Vault mode marker could not be read.",
                exception);
        }
    }

    public static void Write(
        string modePath,
        CipherVaultVaultModeManifest manifest,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(manifest);
        CipherVaultServiceConfiguration.Validate(manifest);
        var fullPath = Path.GetFullPath(modePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw Invalid("Vault mode marker path has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".vault.mode.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    manifest,
                    WriteOptions);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
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
                "Vault mode marker could not be written.",
                exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void EnsureCompatibilityAllowed(string vaultPath)
    {
        var fullVaultPath = Path.GetFullPath(vaultPath);
        var modePath = Path.Combine(fullVaultPath, "vault.mode");
        if (!File.Exists(modePath))
        {
            return;
        }
        throw new VaultException(
            VaultErrorCode.InvalidInput,
            "Compatibility mode cannot open a Vault owned by the strict Windows service.");
    }

    private static string ReadBoundedText(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumModeBytes)
        {
            throw Invalid("Vault mode marker is too large.");
        }
        using var reader = new StreamReader(
            stream,
            detectEncodingFromByteOrderMarks: true);
        var json = reader.ReadToEnd();
        cancellationToken.ThrowIfCancellationRequested();
        return json;
    }

    private static VaultException Invalid(string message)
    {
        return new VaultException(VaultErrorCode.InvalidInput, message);
    }
}
