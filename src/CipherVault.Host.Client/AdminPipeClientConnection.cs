using System.Text.Json;
using CipherVault.Host.Contracts;
using CipherVault.Ipc;
using CipherVault.Security.Win;

namespace CipherVault.Host.Client;

public sealed class AdminPipeClientConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private VerifiedProcessEvidence? _clientEvidence;
    private VerifiedPipeClientConnection? _verifiedConnection;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private bool _disposed;

    private AdminPipeClientConnection(
        AdminPipeClientOptions options,
        VerifiedProcessEvidence clientEvidence,
        VerifiedPipeClientConnection verifiedConnection,
        Guid processSessionId,
        Guid adminSessionId,
        DateTimeOffset expiresAt)
    {
        _clientEvidence = clientEvidence;
        _verifiedConnection = verifiedConnection;
        ProcessSessionId = processSessionId;
        AdminSessionId = adminSessionId;
        ExpiresAt = expiresAt;
    }

    public Guid ProcessSessionId { get; }
    public Guid AdminSessionId { get; }
    public DateTimeOffset ExpiresAt { get; }

    public static async Task<AdminPipeClientConnection> ConnectAsync(
        AdminPipeClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ClientPolicy.ExpectedKind is not (
                VerifiedProcessKind.AdminUi or
                VerifiedProcessKind.AdminCli))
        {
            throw new ArgumentException(
                "Admin client policy must expect UI or CLI.",
                nameof(options));
        }
        if (options.ServerPolicy.ExpectedKind != VerifiedProcessKind.HostService)
        {
            throw new ArgumentException(
                "Admin server policy must expect Host service.",
                nameof(options));
        }

        var clientResult = WindowsProcessIdentityVerifier.Verify(
            (uint)Environment.ProcessId,
            options.ClientPolicy);
        if (clientResult.Status != ProcessVerificationStatus.Verified ||
            clientResult.Evidence is null)
        {
            clientResult.Evidence?.Dispose();
            throw new ProcessIdentityRejectedException(
                clientResult.Status,
                clientResult.ReasonCode ?? "client_identity_rejected");
        }

        VerifiedPipeClientConnection? verifiedConnection = null;
        try
        {
            verifiedConnection = await VerifiedPipeClientConnection.ConnectAsync(
                options.PipeName,
                options.ServerPolicy,
                options.ConnectTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            var clientNonce = PipeNonce.Create();
            await verifiedConnection.Connection.WriteAsync(
                new PipeHelloRequest(PipeProtocol.Version, clientNonce),
                cancellationToken).ConfigureAwait(false);
            var hello = await verifiedConnection.Connection
                .ReadAsync<PipeHelloResponse>(cancellationToken)
                .ConfigureAwait(false);
            ValidateHello(hello, verifiedConnection.ServerIdentity);

            var transcript = PipeHandshakeTranscript.Compute(
                NamedPipeServerFactory.NormalizePipeName(options.PipeName),
                PipeProtocol.Version,
                clientNonce,
                hello.ServerNonceB64Url,
                clientResult.Evidence.Identity,
                verifiedConnection.ServerIdentity);
            await verifiedConnection.Connection.WriteAsync(
                new PipeAuthenticateRequest(
                    clientResult.Evidence.Identity.Kind,
                    transcript),
                cancellationToken).ConfigureAwait(false);
            var auth = await verifiedConnection.Connection
                .ReadAsync<PipeAuthenticateResponse>(cancellationToken)
                .ConfigureAwait(false);
            if (auth.ProcessSessionId == Guid.Empty ||
                auth.AdminSessionId is null ||
                auth.AdminSessionId == Guid.Empty ||
                auth.ExpiresAt <= DateTimeOffset.UtcNow ||
                !string.Equals(
                    auth.OwnerSid,
                    clientResult.Evidence.Identity.UserSid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Admin pipe authentication response is invalid.");
            }

            return new AdminPipeClientConnection(
                options,
                clientResult.Evidence,
                verifiedConnection,
                auth.ProcessSessionId,
                auth.AdminSessionId.Value,
                auth.ExpiresAt);
        }
        catch
        {
            if (verifiedConnection is not null)
            {
                await verifiedConnection.DisposeAsync().ConfigureAwait(false);
            }
            clientResult.Evidence.Dispose();
            throw;
        }
    }

    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string method,
        TRequest payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = _verifiedConnection?.Connection
                ?? throw new ObjectDisposedException(nameof(AdminPipeClientConnection));
            var request = PipeRpcCodec.CreateRequest(method, payload);
            await connection.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var response = await connection.ReadAsync<PipeRpcResponse>(cancellationToken)
                .ConfigureAwait(false);
            if (response.RequestId != request.RequestId)
            {
                throw new InvalidDataException("Admin pipe response request id does not match.");
            }
            if (!response.Ok)
            {
                throw new PipeRpcException(
                    response.Error?.Code ?? "pipe_error",
                    response.Error?.Message ?? "Admin pipe request failed.");
            }
            if (response.Data is null)
            {
                throw new InvalidDataException("Admin pipe response data was empty.");
            }
            return response.Data.Value.Deserialize<TResponse>(JsonOptions)
                ?? throw new InvalidDataException("Admin pipe response data was empty.");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public Task<AdminPipeStatusData> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        return CallAsync<object, AdminPipeStatusData>(
            "admin.status",
            new { },
            cancellationToken);
    }

    public Task<AdminSecurityDiagnosticsData> GetSecurityDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return CallAsync<object, AdminSecurityDiagnosticsData>(
            "admin.security.diagnostics",
            new { },
            cancellationToken);
    }

    public Task<IReadOnlyList<AdminStrictBridgeData>> ListBridgesAsync(
        CancellationToken cancellationToken = default)
    {
        return CallAsync<object, IReadOnlyList<AdminStrictBridgeData>>(
            "admin.agent.bridges.list",
            new { },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_verifiedConnection is not null)
        {
            await _verifiedConnection.DisposeAsync().ConfigureAwait(false);
            _verifiedConnection = null;
        }
        Interlocked.Exchange(ref _clientEvidence, null)?.Dispose();
        _requestGate.Dispose();
    }

    private static void ValidateHello(
        PipeHelloResponse hello,
        VerifiedProcessIdentity serverIdentity)
    {
        if (!string.Equals(
                hello.ProtocolVersion,
                PipeProtocol.Version,
                StringComparison.Ordinal) ||
            !PipeNonce.IsValid(hello.ServerNonceB64Url) ||
            string.IsNullOrWhiteSpace(hello.ServiceInstanceId) ||
            hello.ServerProcessId != serverIdentity.ProcessId ||
            hello.ServerProcessStartedAt != serverIdentity.ProcessStartedAt ||
            !string.Equals(
                hello.ServiceSid,
                serverIdentity.UserSid,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                hello.ServerImageSha256,
                serverIdentity.ImageSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                hello.ServerSignerThumbprint,
                serverIdentity.SignerThumbprint ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Admin pipe hello response is invalid.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class PipeRpcException : Exception
{
    public PipeRpcException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
