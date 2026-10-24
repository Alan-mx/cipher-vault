using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Ipc;
using CipherVault.Security.Win;

namespace CipherVault.Host.Client;

public sealed record BridgePipeClientOptions(
    string PipeName,
    string ClientId,
    McpAccessMode AccessMode,
    IReadOnlySet<string> RequestedScopes,
    ProcessVerificationPolicy ClientPolicy,
    ProcessVerificationPolicy ServerPolicy,
    int ConnectTimeoutMilliseconds = 5_000);

public sealed class BridgePipeClientConnection :
    IBridgePipeClientConnection
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly IReadOnlySet<string> _requestedScopes;
    private VerifiedProcessEvidence? _clientEvidence;
    private VerifiedPipeClientConnection? _connection;
    private int _faulted;
    private bool _disposed;

    private BridgePipeClientConnection(
        VerifiedProcessEvidence clientEvidence,
        VerifiedPipeClientConnection connection,
        IReadOnlySet<string> requestedScopes,
        DateTimeOffset expiresAt)
    {
        _clientEvidence = clientEvidence;
        _connection = connection;
        _requestedScopes = requestedScopes;
        ExpiresAt = expiresAt;
    }

    public DateTimeOffset ExpiresAt { get; }
    public bool IsUsable =>
        !_disposed &&
        Volatile.Read(ref _faulted) == 0 &&
        ExpiresAt > DateTimeOffset.UtcNow;

    public static async Task<BridgePipeClientConnection> ConnectAsync(
        BridgePipeClientOptions options,
        CancellationToken cancellationToken = default)
    {
        var client = WindowsProcessIdentityVerifier.Verify(
            (uint)Environment.ProcessId,
            options.ClientPolicy);
        if (client.Status != ProcessVerificationStatus.Verified ||
            client.Evidence is null)
        {
            client.Evidence?.Dispose();
            throw new ProcessIdentityRejectedException(
                client.Status,
                client.ReasonCode ?? "bridge_identity_rejected");
        }
        VerifiedPipeClientConnection? connection = null;
        try
        {
            connection = await VerifiedPipeClientConnection.ConnectAsync(
                options.PipeName,
                options.ServerPolicy,
                options.ConnectTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            var nonce = PipeNonce.Create();
            await connection.Connection.WriteAsync(
                new PipeHelloRequest(PipeProtocol.Version, nonce),
                cancellationToken).ConfigureAwait(false);
            var hello = await connection.Connection.ReadAsync<PipeHelloResponse>(
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    hello.ProtocolVersion,
                    PipeProtocol.Version,
                    StringComparison.Ordinal) ||
                !PipeNonce.IsValid(hello.ServerNonceB64Url) ||
                string.IsNullOrWhiteSpace(hello.ServiceInstanceId) ||
                hello.ServerProcessId != connection.ServerIdentity.ProcessId ||
                hello.ServerProcessStartedAt !=
                    connection.ServerIdentity.ProcessStartedAt ||
                !string.Equals(
                    hello.ServiceSid,
                    connection.ServerIdentity.UserSid,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    hello.ServerImageSha256,
                    connection.ServerIdentity.ImageSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    hello.ServerSignerThumbprint,
                    connection.ServerIdentity.SignerThumbprint ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Bridge pipe hello response is invalid.");
            }
            var transcript = PipeHandshakeTranscript.Compute(
                NamedPipeServerFactory.NormalizePipeName(options.PipeName),
                PipeProtocol.Version,
                nonce,
                hello.ServerNonceB64Url,
                client.Evidence.Identity,
                connection.ServerIdentity);
            await connection.Connection.WriteAsync(
                new PipeAuthenticateRequest(
                    VerifiedProcessKind.McpBridge,
                    transcript),
                cancellationToken).ConfigureAwait(false);
            var auth = await connection.Connection.ReadAsync<
                PipeAuthenticateResponse>(cancellationToken).ConfigureAwait(false);
            if (auth.ProcessSessionId == Guid.Empty ||
                auth.AdminSessionId is not null ||
                auth.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidDataException(
                    "Bridge authentication response is invalid.");
            }
            return new BridgePipeClientConnection(
                client.Evidence,
                connection,
                options.RequestedScopes,
                auth.ExpiresAt);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            client.Evidence.Dispose();
            throw;
        }
    }

    public async Task<BridgeGrantData> RequestGrantAsync(
        CancellationToken cancellationToken = default)
    {
        var grant = await CallAsync<BridgeGrantRequest, BridgeGrantData>(
            "bridge.grant.request",
            new BridgeGrantRequest(
                _requestedScopes
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()),
            cancellationToken).ConfigureAwait(false);
        return grant;
    }

    public Task<string> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        return CallAsync<BridgeToolCallRequest, string>(
            "bridge.tools.call",
            new BridgeToolCallRequest(
                toolName,
                JsonSerializer.SerializeToElement(arguments, JsonOptions)),
            cancellationToken);
    }

    public Task<BridgeStatusData> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        return CallAsync<object, BridgeStatusData>(
            "bridge.status",
            new { },
            cancellationToken);
    }

    public Task<PipePingResponse> PingAsync(
        CancellationToken cancellationToken = default)
    {
        return CallAsync<object, PipePingResponse>(
            PipeProtocol.PingMethod,
            new { },
            cancellationToken);
    }

    public Task<BridgeSessionCloseData> CloseSessionAsync(
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<BridgeSessionCloseRequest, BridgeSessionCloseData>(
            "bridge.session.close",
            new BridgeSessionCloseRequest(reason),
            cancellationToken);
    }

    private async Task<TResponse> CallAsync<TRequest, TResponse>(
        string method,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = _connection?.Connection
                ?? throw new ObjectDisposedException(
                    nameof(BridgePipeClientConnection));
            var request = PipeRpcCodec.CreateRequest(method, payload);
            await connection.WriteAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var response = await connection.ReadAsync<PipeRpcResponse>(
                cancellationToken).ConfigureAwait(false);
            if (!response.Ok)
            {
                throw new PipeRpcException(
                    response.Error?.Code ?? "bridge_error",
                    response.Error?.Message ?? "Bridge request failed.");
            }
            if (response.Data is null)
            {
                throw new InvalidDataException("Bridge response data was empty.");
            }
            return response.Data.Value.Deserialize<TResponse>(JsonOptions)
                ?? throw new InvalidDataException(
                    "Bridge response data was empty.");
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException ||
            exception is PipeRpcException
            {
                Code: "bridge_session_expired"
            })
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
        Interlocked.Exchange(ref _clientEvidence, null)?.Dispose();
        _requestGate.Dispose();
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(
            _disposed || Volatile.Read(ref _faulted) != 0,
            this);
        if (ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw new PipeRpcException(
                "bridge_session_expired",
                "The Bridge session has expired.");
        }
    }
}
