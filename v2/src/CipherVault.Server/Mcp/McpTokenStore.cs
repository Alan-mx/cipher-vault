using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Server.Hosting;

namespace CipherVault.Server.Mcp;

public sealed class McpTokenStore
{
    internal const int TokenBytes = 32;
    internal const int MaxTokenFileBytes = 4096;
    internal const int CurrentFileVersion = 1;
    internal const string HashAlgorithm = "sha256";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    private readonly string tokenPath;
    private readonly TimeProvider timeProvider;
    private readonly Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>
        writeFileAsync;
    private readonly SemaphoreSlim rotationGate = new(1, 1);
    private McpTokenRuntimeState current;

    internal McpTokenStore(
        string tokenPath,
        McpTokenRuntimeState initialState,
        TimeProvider timeProvider,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? writeFileAsync = null)
    {
        this.tokenPath = tokenPath;
        current = initialState;
        this.timeProvider = timeProvider;
        this.writeFileAsync = writeFileAsync ??
            ((path, content, cancellationToken) =>
                AtomicFileWriter.WriteAsync(path, content, cancellationToken));
    }

    public static async Task<McpTokenStore> LoadAsync(
        string tokenPath,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var provider = timeProvider ?? TimeProvider.System;
        if (!File.Exists(tokenPath))
        {
            return new McpTokenStore(
                tokenPath,
                McpTokenRuntimeState.Unconfigured,
                provider);
        }

        try
        {
            var info = new FileInfo(tokenPath);
            if (info.Length is <= 0 or > MaxTokenFileBytes)
            {
                throw InvalidTokenFile();
            }

            VaultFilePermissions.ProtectFile(tokenPath);
            var bytes = await File.ReadAllBytesAsync(
                tokenPath,
                cancellationToken);
            if (bytes.Length > MaxTokenFileBytes)
            {
                throw InvalidTokenFile();
            }

            var file = JsonSerializer.Deserialize<McpTokenFile>(
                bytes,
                JsonOptions) ?? throw InvalidTokenFile();
            var state = ToRuntimeState(file);
            return new McpTokenStore(tokenPath, state, provider);
        }
        catch (ServerStartupException)
        {
            throw;
        }
        catch (VaultException exception)
        {
            throw TokenStartupFailure(exception);
        }
        catch (JsonException exception)
        {
            throw TokenStartupFailure(exception);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or FormatException)
        {
            throw TokenStartupFailure(exception);
        }
    }

    public McpTokenStatus GetStatus()
    {
        var state = Volatile.Read(ref current);
        return new McpTokenStatus(
            state.Configured,
            state.Version,
            state.CreatedAtUtc,
            state.RotatedAtUtc);
    }

    public bool TryAuthenticate(string? rawToken)
    {
        if (string.IsNullOrEmpty(rawToken) || rawToken.Length > 1024)
        {
            return false;
        }

        var state = Volatile.Read(ref current);
        if (!state.Configured || state.Digest.Length != 32)
        {
            return false;
        }

        var candidate = SHA256.HashData(
            Encoding.UTF8.GetBytes(rawToken));
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                candidate,
                state.Digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    public async Task<McpTokenRotateResponse> RotateAsync(
        CancellationToken cancellationToken = default)
    {
        await rotationGate.WaitAsync(cancellationToken);
        try
        {
            var previous = Volatile.Read(ref current);
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var rawToken = CreateToken();
            var digest = SHA256.HashData(
                Encoding.UTF8.GetBytes(rawToken));
            var createdAt = previous.CreatedAtUtc ?? now;
            var file = new McpTokenFile(
                CurrentFileVersion,
                HashAlgorithm,
                Convert.ToHexString(digest).ToLowerInvariant(),
                createdAt,
                now);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(
                file,
                JsonOptions);

            try
            {
                await writeFileAsync(
                    tokenPath,
                    serialized,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or VaultException
                    or System.Security.SecurityException)
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Unable to persist the MCP token.",
                    details: new Dictionary<string, object?>
                    {
                        ["errorCode"] = "mcp_token_write_failed"
                    });
            }

            var next = new McpTokenRuntimeState(
                true,
                CurrentFileVersion,
                digest,
                createdAt,
                now);
            Volatile.Write(ref current, next);
            return new McpTokenRotateResponse(
                rawToken,
                CurrentFileVersion,
                createdAt,
                now);
        }
        finally
        {
            rotationGate.Release();
        }
    }

    internal static McpTokenRuntimeState ToRuntimeState(McpTokenFile file)
    {
        if (file.Version != CurrentFileVersion
            || !string.Equals(
                file.HashAlgorithm,
                HashAlgorithm,
                StringComparison.Ordinal)
            || !IsLowerHex(file.Digest)
            || file.CreatedAtUtc > file.RotatedAtUtc)
        {
            throw InvalidTokenFile();
        }

        var digest = Convert.FromHexString(file.Digest);
        if (digest.Length != 32)
        {
            CryptographicOperations.ZeroMemory(digest);
            throw InvalidTokenFile();
        }

        return new McpTokenRuntimeState(
            true,
            file.Version,
            digest,
            file.CreatedAtUtc.ToUniversalTime(),
            file.RotatedAtUtc.ToUniversalTime());
    }

    internal static string CreateToken() =>
        Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(TokenBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static string HashToken(string rawToken) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)))
            .ToLowerInvariant();

    private static bool IsLowerHex(string value) =>
        value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static ServerStartupException InvalidTokenFile() =>
        new(
            ServerStartupCode.McpTokenFailure,
            "MCP token state is invalid.");

    private static ServerStartupException TokenStartupFailure(
        Exception exception) =>
        new(
            ServerStartupCode.McpTokenFailure,
            "Unable to load MCP token state.",
            exception);
}
