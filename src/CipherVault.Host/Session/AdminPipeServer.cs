using System.IO;
using System.IO.Pipes;
using StringCompare = System.StringComparison;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Ipc;
using CipherVault.Security.Win;

namespace CipherVault.Host.Session;

public sealed class AdminPipeServer : IAsyncDisposable
{
    private readonly AdminPipeServerOptions _options;
    private readonly AdminSessionStore _sessions;
    private readonly NamedPipeServerStream _server;
    private int _accepted;
    private bool _disposed;

    public AdminPipeServer(AdminPipeServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PipeName);
        ArgumentNullException.ThrowIfNull(options.Security);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SecurityMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OwnerSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceSid);
        ArgumentNullException.ThrowIfNull(options.ServiceIdentity);
        if (options.ServiceIdentity.Kind != VerifiedProcessKind.HostService)
        {
            throw new ArgumentException(
                "Admin pipe service identity must be a Host service.",
                nameof(options));
        }
        if (options.AllowedClientPolicies.Count == 0)
        {
            throw new ArgumentException(
                "At least one Admin client process policy is required.",
                nameof(options));
        }
        _sessions = new AdminSessionStore(
            idleLifetime: options.SessionIdleLifetime,
            hardLifetime: options.SessionHardLifetime);
        _server = NamedPipeServerFactory.Create(options.PipeName, options.Security);
    }

    public AdminSessionStore Sessions => _sessions;

    public async Task<AdminSession?> AcceptOnceAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _accepted, 1) != 0)
        {
            throw new InvalidOperationException(
                "Admin pipe server accepts only one connection per instance.");
        }

        await _server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new PipeJsonConnection(_server);
        using var clientEvidence = VerifyClientProcess();
        var clientIdentity = clientEvidence.Identity;

        var clientHello = await connection.ReadAsync<PipeHelloRequest>(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                clientHello.ProtocolVersion,
                PipeProtocol.Version,
                StringCompare.Ordinal) ||
            !PipeNonce.IsValid(clientHello.ClientNonceB64Url))
        {
            throw new InvalidDataException("Pipe hello was invalid.");
        }

        var serverNonce = PipeNonce.Create();
        await connection.WriteAsync(
            new PipeHelloResponse(
                PipeProtocol.Version,
                serverNonce,
                _options.ServiceInstanceId,
                _options.ServiceSid,
                _options.ServiceIdentity.ProcessId,
                _options.ServiceIdentity.ProcessStartedAt,
                _options.ServiceIdentity.ImageSha256,
                _options.ServiceIdentity.SignerThumbprint ?? string.Empty),
            cancellationToken).ConfigureAwait(false);

        var authRequest = await connection.ReadAsync<PipeAuthenticateRequest>(
            cancellationToken).ConfigureAwait(false);
        if (authRequest.ClientKind != clientIdentity.Kind)
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "client_kind_mismatch");
        }
        var expectedTranscript = PipeHandshakeTranscript.Compute(
            NamedPipeServerFactory.NormalizePipeName(_options.PipeName),
            PipeProtocol.Version,
            clientHello.ClientNonceB64Url,
            serverNonce,
            clientIdentity,
            _options.ServiceIdentity);
        if (!PipeHandshakeTranscript.FixedTimeEquals(
                expectedTranscript,
                authRequest.TranscriptHashB64Url))
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "handshake_transcript_mismatch");
        }

        var adminSession = _sessions.Create(
            clientIdentity,
            _options.OwnerSid,
            _options.ServiceSid);
        await connection.WriteAsync(
            new PipeAuthenticateResponse(
                adminSession.ProcessSessionId,
                adminSession.SessionId,
                adminSession.OwnerSid,
                clientIdentity.SessionId,
                adminSession.IdleExpiresAt),
            cancellationToken).ConfigureAwait(false);

        try
        {
            await HandleRequestsAsync(
                connection,
                adminSession.SessionId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessions.Revoke(adminSession.SessionId, "pipe-disconnected");
        }
        return adminSession;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _sessions.RevokeAll("service-disposed");
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandleRequestsAsync(
        PipeJsonConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            PipeRpcRequest request;
            try
            {
                request = await connection.ReadAsync<PipeRpcRequest>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            PipeRpcResponse response;
            if (!string.Equals(
                    request.ProtocolVersion,
                    PipeProtocol.Version,
                    StringCompare.Ordinal))
            {
                response = PipeRpcCodec.Failure(
                    request.RequestId,
                    "protocol_mismatch",
                    "Pipe protocol version is unsupported.");
            }
            else if (!_sessions.TryGetActive(sessionId, out var session))
            {
                response = PipeRpcCodec.Failure(
                    request.RequestId,
                    "admin_session_expired",
                    "Admin session has expired.");
            }
            else
            {
                response = await HandleRequestAsync(
                    session,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }

            await connection.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.Ok &&
                string.Equals(
                    response.Error?.Code,
                    "admin_session_expired",
                    StringCompare.Ordinal))
            {
                return;
            }
        }
    }

    private async Task<PipeRpcResponse> HandleRequestAsync(
        AdminSession session,
        PipeRpcRequest request,
        CancellationToken cancellationToken)
    {
        if (string.Equals(request.Method, "admin.status", StringCompare.Ordinal))
        {
            try
            {
                var status = await (_options.StatusProvider?.Invoke(
                        session,
                        cancellationToken) ??
                    Task.FromResult(CreateDefaultStatus()))
                    .ConfigureAwait(false);
                return PipeRpcCodec.Success(request.RequestId, status);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                return PipeRpcCodec.Failure(
                    request.RequestId,
                    "storage_error",
                    exception.Message);
            }
        }

        return PipeRpcCodec.Failure(
            request.RequestId,
            "unsupported_method",
            $"Unsupported Admin method: {request.Method}");
    }

    private VerifiedProcessEvidence VerifyClientProcess()
    {
        var clientPid = NamedPipeProcessIdentity.GetClientProcessId(
            _server.SafePipeHandle);
        ProcessVerificationResult? lastResult = null;
        foreach (var policy in _options.AllowedClientPolicies)
        {
            var result = WindowsProcessIdentityVerifier.Verify(clientPid, policy);
            if (result.Status == ProcessVerificationStatus.Verified &&
                result.Evidence is not null)
            {
                return result.Evidence;
            }
            lastResult = result;
        }
        throw new ProcessIdentityRejectedException(
            lastResult?.Status ?? ProcessVerificationStatus.IdentityMismatch,
            lastResult?.ReasonCode ?? "client_identity_rejected");
    }

    private AdminPipeStatusData CreateDefaultStatus()
    {
        return new AdminPipeStatusData(
            _options.ServiceVersion,
            PipeProtocol.Version,
            _options.SecurityMode,
            BrokerAvailable: false,
            new HostSessionStatusData(
                "locked",
                SessionId: null,
                StartedAt: null,
                LastUserActivityAt: null,
                IdleExpiresAt: null,
                HardExpiresAt: null,
                LockReason: null,
                ActiveLeases: 0,
                ConnectedClients: 0,
                RecoveryVerified: false));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
