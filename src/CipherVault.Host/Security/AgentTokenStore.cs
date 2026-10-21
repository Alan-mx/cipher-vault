using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Security;

public sealed class AgentTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ResolvedCipherVaultHostConfig _configuration;
    private readonly ISecurityEventLog _securityLog;
    private List<AgentClientPolicy> _clients;

    public AgentTokenStore(
        ResolvedCipherVaultHostConfig configuration,
        ISecurityEventLog securityLog)
    {
        _configuration = configuration;
        _securityLog = securityLog;
        _clients = configuration.Clients
            .Select(Clone)
            .ToList();
    }

    public Task<IReadOnlyList<HostClientData>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            IReadOnlyList<HostClientData> result = _clients
                .Select(ToData)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<AuthenticatedHostClient?> AuthenticateAsync(
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return Task.FromResult<AuthenticatedHostClient?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        try
        {
            lock (_sync)
            {
                foreach (var client in _clients)
                {
                    if (!client.Enabled ||
                        !CryptographicOperations.FixedTimeEquals(
                            presentedHash,
                            client.TokenHash))
                    {
                        continue;
                    }

                    return Task.FromResult<AuthenticatedHostClient?>(
                        new AuthenticatedHostClient(
                            client.ClientId,
                            client.AccessMode,
                            client.AllowLock));
                }
            }
            return Task.FromResult<AuthenticatedHostClient?>(null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(presentedHash);
        }
    }

    public async Task<HostCreateClientData> CreateAsync(
        HostCreateClientRequest request,
        CancellationToken cancellationToken = default)
    {
        var clientId = GetNonEmpty(request.Id)
            ?? throw new HostException(
                HostErrorCode.InvalidInput,
                "Client id is required.");
        if (!IsValidClientId(clientId))
        {
            throw new HostException(
                HostErrorCode.InvalidInput,
                "Client id is invalid.");
        }
        var accessMode = ParseAccessMode(request.AccessMode)
            ?? McpAccessMode.ReadOnly;
        var (rawToken, tokenHash) = GenerateToken();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AgentClientPolicy> next;
            lock (_sync)
            {
                if (_clients.Any(client => string.Equals(
                        client.ClientId,
                        clientId,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    throw new HostException(
                        HostErrorCode.AlreadyExists,
                        $"Client '{clientId}' already exists.");
                }
                next = [.. _clients];
            }

            var policy = new AgentClientPolicy(
                clientId,
                tokenHash,
                accessMode,
                request.AllowLock ?? false,
                Enabled: true);
            next.Add(policy);
            await PersistAsync(next, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _clients = next;
            }
            return new HostCreateClientData(ToData(policy), rawToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<HostClientData> UpdateAsync(
        string clientId,
        HostUpdateClientRequest request,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AgentClientPolicy> next;
            AgentClientPolicy updated;
            lock (_sync)
            {
                var index = _clients.FindIndex(client => string.Equals(
                    client.ClientId,
                    clientId,
                    StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    throw new HostException(
                        HostErrorCode.NotFound,
                        $"Client '{clientId}' was not found.");
                }

                var current = _clients[index];
                updated = current with
                {
                    AccessMode = ParseAccessMode(request.AccessMode)
                        ?? current.AccessMode,
                    AllowLock = request.AllowLock ?? current.AllowLock,
                    Enabled = request.Enabled ?? current.Enabled
                };
                next = [.. _clients];
                next[index] = updated;
            }

            await PersistAsync(next, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _clients = next;
            }
            return ToData(updated);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<HostCreateClientData> RotateAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AgentClientPolicy> next;
            AgentClientPolicy rotated;
            string rawToken;
            lock (_sync)
            {
                var index = _clients.FindIndex(client => string.Equals(
                    client.ClientId,
                    clientId,
                    StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    throw new HostException(
                        HostErrorCode.NotFound,
                        $"Client '{clientId}' was not found.");
                }

                byte[] tokenHash;
                (rawToken, tokenHash) = GenerateToken();
                rotated = _clients[index] with { TokenHash = tokenHash };
                next = [.. _clients];
                next[index] = rotated;
            }

            await PersistAsync(next, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _clients = next;
            }
            return new HostCreateClientData(ToData(rotated), rawToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteAsync(
        string clientId,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AgentClientPolicy> next;
            lock (_sync)
            {
                var index = _clients.FindIndex(client => string.Equals(
                    client.ClientId,
                    clientId,
                    StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    throw new HostException(
                        HostErrorCode.NotFound,
                        $"Client '{clientId}' was not found.");
                }
                next = [.. _clients];
                next.RemoveAt(index);
            }

            await PersistAsync(next, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _clients = next;
            }
            await SecurityLogSafety.TryWriteAsync(
                _securityLog,
                new HostSecurityLogEvent(
                    DateTimeOffset.UtcNow,
                    "token_revoked",
                    clientId,
                    ConnectionId: null,
                    requestId,
                    "success",
                    ReasonCode: null),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal static string ToAccessMode(McpAccessMode accessMode)
    {
        return accessMode == McpAccessMode.ReadOnly
            ? "readOnly"
            : "readWrite";
    }

    private async Task PersistAsync(
        IReadOnlyList<AgentClientPolicy> clients,
        CancellationToken cancellationToken)
    {
        try
        {
            var configPath = _configuration.ConfigPath;
            JsonObject root;
            if (File.Exists(configPath))
            {
                var existing = await File.ReadAllTextAsync(configPath, cancellationToken)
                    .ConfigureAwait(false);
                root = JsonNode.Parse(existing) as JsonObject
                    ?? throw new JsonException("Host config root must be an object.");
            }
            else
            {
                root = CreateDefaultConfigRoot();
            }

            var clientArray = new JsonArray();
            foreach (var client in clients)
            {
                clientArray.Add(new JsonObject
                {
                    ["id"] = client.ClientId,
                    ["tokenHash"] = "sha256:" + Convert.ToHexString(client.TokenHash),
                    ["accessMode"] = ToAccessMode(client.AccessMode),
                    ["allowLock"] = client.AllowLock,
                    ["enabled"] = client.Enabled
                });
            }
            root["clients"] = clientArray;

            var directory = Path.GetDirectoryName(configPath)
                ?? throw new HostException(
                    HostErrorCode.InvalidInput,
                    "Config path has no parent directory.");
            Directory.CreateDirectory(directory);
            VaultFileSecurity.ProtectDirectory(directory);
            var temporaryPath = configPath + $".tmp-{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    root.ToJsonString(JsonOptions),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
                VaultFileSecurity.ProtectFile(temporaryPath);
                File.Move(temporaryPath, configPath, overwrite: true);
                VaultFileSecurity.ProtectFile(configPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (HostException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            IOException or
            UnauthorizedAccessException or
            CryptographicException or
            VaultException)
        {
            throw new HostException(
                HostErrorCode.StorageError,
                "Host configuration could not be written.",
                exception);
        }
    }

    private JsonObject CreateDefaultConfigRoot()
    {
        return new JsonObject
        {
            ["configVersion"] = CipherVaultHostConfiguration.CurrentConfigVersion,
            ["vaultPath"] = _configuration.VaultPath,
            ["session"] = new JsonObject
            {
                ["idleMinutes"] = (int)_configuration.SessionOptions.IdleTimeout.TotalMinutes,
                ["hardHours"] = (int)_configuration.SessionOptions.HardLifetime.TotalHours
            },
            ["host"] = new JsonObject
            {
                ["bindAddress"] = _configuration.Endpoint.BindAddress,
                ["port"] = _configuration.Endpoint.Port,
                ["mcpPath"] = _configuration.Endpoint.McpPath,
                ["adminPath"] = _configuration.Endpoint.AdminPath,
                ["maxRequestBodyBytes"] = _configuration.Endpoint.MaxRequestBodyBytes,
                ["allowedOrigins"] = new JsonArray(
                    _configuration.Endpoint.AllowedOrigins
                        .Select(origin => JsonValue.Create(origin))
                        .ToArray<JsonNode?>())
            }
        };
    }

    private static HostClientData ToData(AgentClientPolicy client)
    {
        return new HostClientData(
            client.ClientId,
            ToAccessMode(client.AccessMode),
            client.AllowLock,
            client.Enabled);
    }

    private static AgentClientPolicy Clone(AgentClientPolicy client)
    {
        return new AgentClientPolicy(
            client.ClientId,
            [.. client.TokenHash],
            client.AccessMode,
            client.AllowLock,
            client.Enabled);
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
            _ => throw new HostException(
                HostErrorCode.InvalidInput,
                "accessMode must be readOnly or readWrite.")
        };
    }

    private static bool IsValidClientId(string clientId)
    {
        return clientId.Length is >= 1 and <= 64 &&
            clientId.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-');
    }

    private static string? GetNonEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static (string RawToken, byte[] TokenHash) GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var rawToken = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return (rawToken, SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
