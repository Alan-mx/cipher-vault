using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Ipc;
using CipherVault.Security.Win;

namespace CipherVault.Host.Session;

public sealed class BridgePipeServer : IAsyncDisposable
{
    private readonly BridgePipeServerOptions _options;
    private readonly NamedPipeServerStream _server;
    private int _accepted;
    private bool _disposed;

    public BridgePipeServer(BridgePipeServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PipeName);
        ArgumentNullException.ThrowIfNull(options.Security);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OwnerSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientId);
        ArgumentNullException.ThrowIfNull(options.ServiceIdentity);
        ArgumentNullException.ThrowIfNull(options.Handler);
        if (options.ServiceIdentity.Kind != VerifiedProcessKind.HostService)
        {
            throw new ArgumentException(
                "Bridge pipe service identity must be the Host service.",
                nameof(options));
        }
        if (options.AllowedClientPolicy.ExpectedKind !=
                VerifiedProcessKind.McpBridge ||
            options.BridgeSessionLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Bridge pipe client policy or session lifetime is invalid.",
                nameof(options));
        }
        _server = NamedPipeServerFactory.Create(
            options.PipeName,
            options.Security);
    }

    public async Task<BridgeProcessSession> AcceptOnceAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _accepted, 1) != 0)
        {
            throw new InvalidOperationException(
                "Bridge pipe server accepts only one connection per instance.");
        }

        await _server.WaitForConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = new PipeJsonConnection(_server);
        await using var lifetime = new BridgeConnectionLifetime(_server);
        lifetime.StartMonitoring(cancellationToken);
        using var clientEvidence = VerifyClientProcess();
        var clientIdentity = clientEvidence.Identity;
        if (clientIdentity.SessionId == 0 ||
            !string.Equals(
                clientIdentity.UserSid,
                _options.OwnerSid,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "bridge_owner_mismatch");
        }

        var hello = await connection.ReadAsync<PipeHelloRequest>(lifetime.Token)
            .ConfigureAwait(false);
        if (!string.Equals(
                hello.ProtocolVersion,
                PipeProtocol.Version,
                StringComparison.Ordinal) ||
            !PipeNonce.IsValid(hello.ClientNonceB64Url))
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "bridge_hello_invalid");
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
            lifetime.Token).ConfigureAwait(false);

        var authenticate = await connection.ReadAsync<PipeAuthenticateRequest>(
            lifetime.Token).ConfigureAwait(false);
        var expectedTranscript = PipeHandshakeTranscript.Compute(
            NamedPipeServerFactory.NormalizePipeName(_options.PipeName),
            PipeProtocol.Version,
            hello.ClientNonceB64Url,
            serverNonce,
            clientIdentity,
            _options.ServiceIdentity);
        if (authenticate.ClientKind != VerifiedProcessKind.McpBridge ||
            !PipeHandshakeTranscript.FixedTimeEquals(
                expectedTranscript,
                authenticate.TranscriptHashB64Url))
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "bridge_handshake_mismatch");
        }

        var now = DateTimeOffset.UtcNow;
        var session = new BridgeProcessSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            clientIdentity,
            _options.OwnerSid,
            _options.ServiceSid,
            _options.ClientId,
            _options.AccessMode,
            now,
            now + _options.BridgeSessionLifetime);
        await connection.WriteAsync(
            new PipeAuthenticateResponse(
                session.ProcessSessionId,
                AdminSessionId: null,
                session.OwnerSid,
                clientIdentity.SessionId,
                session.ExpiresAt),
            lifetime.Token).ConfigureAwait(false);

        var bridgeSession = new BridgePipeSession(
            _options,
            session,
            connection,
            lifetime.Token);
        try
        {
            await bridgeSession.RunAsync(lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            await bridgeSession.DisposeAsync().ConfigureAwait(false);
            await _options.Handler.CloseSessionAsync(
                session.ProcessSessionId,
                "bridge-disconnected",
                CancellationToken.None).ConfigureAwait(false);
        }
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private VerifiedProcessEvidence VerifyClientProcess()
    {
        var clientPid = NamedPipeProcessIdentity.GetClientProcessId(
            _server.SafePipeHandle);
        var result = WindowsProcessIdentityVerifier.Verify(
            clientPid,
            _options.AllowedClientPolicy);
        if (result.Status == ProcessVerificationStatus.Verified &&
            result.Evidence is not null)
        {
            return result.Evidence;
        }
        result.Evidence?.Dispose();
        throw new ProcessIdentityRejectedException(
            result.Status,
            result.ReasonCode ?? "bridge_identity_rejected");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
