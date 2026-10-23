using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Text.Json;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Ipc;
using CipherVault.Security.Win;
using CipherVault.Core.Session;

namespace CipherVault.Host.Session;

public sealed record BrokerPipeServerOptions(
    string PipeName,
    PipeSecurity Security,
    string ServiceInstanceId,
    string ServiceVersion,
    VerifiedProcessIdentity ServiceIdentity,
    IReadOnlyList<ProcessVerificationPolicy> AllowedClientPolicies,
    string OwnerSid,
    string ServiceSid,
    TimeSpan SessionLifetime,
    bool RequireInteractiveSession = true,
    BrokerActivityProvider? ActivityProvider = null,
    OwnerSessionRegistry? OwnerSessions = null);

public sealed class BrokerPipeConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly PipeJsonConnection _connection;
    private readonly BrokerActivityProvider _activityProvider;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PipeRpcResponse>>
        _pending = new();
    private bool _disposed;

    internal BrokerPipeConnection(
        PipeJsonConnection connection,
        VerifiedProcessIdentity clientIdentity,
        BrokerCapabilitiesData capabilities,
        Guid processSessionId,
        DateTimeOffset expiresAt,
        BrokerActivityProvider activityProvider)
    {
        _connection = connection;
        ClientIdentity = clientIdentity;
        Capabilities = capabilities;
        ProcessSessionId = processSessionId;
        ExpiresAt = expiresAt;
        _activityProvider = activityProvider;
    }

    public VerifiedProcessIdentity ClientIdentity { get; }
    public BrokerCapabilitiesData Capabilities { get; }
    public Guid ProcessSessionId { get; }
    public DateTimeOffset ExpiresAt { get; }
    public SystemActivitySnapshot ActivitySnapshot =>
        _activityProvider.GetSnapshot();

    public void ReportActivity(BrokerActivityReport report)
    {
        if (report.SessionId != ClientIdentity.SessionId)
        {
            throw new InvalidDataException(
                "Broker activity session does not match the verified process.");
        }
        _activityProvider.Report(report);
    }

    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string method,
        TRequest payload,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var request = PipeRpcCodec.CreateRequest(method, payload);
        var completion = new TaskCompletionSource<PipeRpcResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.RequestId, completion))
        {
            throw new InvalidOperationException(
                "Broker request id was already pending.");
        }
        try
        {
            await _connection.WriteAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Broker request has expired.");
            }
            var response = await completion.Task
                .WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
            if (!response.Ok)
            {
                throw new AdminPipeRpcException(
                    response.Error?.Code ?? "broker_error",
                    response.Error?.Message ?? "Broker request failed.");
            }
            if (response.Data is null)
            {
                throw new InvalidDataException(
                    "Broker response data was empty.");
            }
            return response.Data.Value.Deserialize<TResponse>(
                    new System.Text.Json.JsonSerializerOptions(
                        System.Text.Json.JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException(
                    "Broker response data was empty.");
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await _connection.ReadAsync<JsonElement>(
                    cancellationToken).ConfigureAwait(false);
                if (frame.TryGetProperty("method", out var method))
                {
                    if (!string.Equals(
                            method.GetString(),
                            "broker.activity.report",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Unsupported Broker notification.");
                    }
                    var request = frame.Deserialize<PipeRpcRequest>(JsonOptions)
                        ?? throw new InvalidDataException(
                            "Broker activity notification was empty.");
                    ReportActivity(
                        PipeRpcCodec.ReadPayload<BrokerActivityReport>(request));
                    continue;
                }
                var response = frame.Deserialize<PipeRpcResponse>(JsonOptions)
                    ?? throw new InvalidDataException(
                        "Broker response was empty.");
                if (_pending.TryRemove(response.RequestId, out var completion))
                {
                    completion.TrySetResult(response);
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            FailPending(new OperationCanceledException(cancellationToken));
        }
        catch (EndOfStreamException exception)
        {
            FailPending(exception);
        }
        catch (IOException exception)
        {
            FailPending(exception);
        }
        catch (Exception exception)
        {
            FailPending(exception);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _activityProvider.Clear();
        FailPending(new ObjectDisposedException(nameof(BrokerPipeConnection)));
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class BrokerPipeServer : IAsyncDisposable
{
    private const string Capability = "credential-prompt.v1";
    private readonly BrokerPipeServerOptions _options;
    private readonly NamedPipeServerStream _server;
    private int _accepted;
    private bool _disposed;

    public BrokerPipeServer(BrokerPipeServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceSid);
        if (options.ServiceIdentity.Kind != VerifiedProcessKind.HostService)
        {
            throw new ArgumentException(
                "Broker pipe service identity must be the Host service.",
                nameof(options));
        }
        if (options.AllowedClientPolicies.Count == 0 ||
            options.AllowedClientPolicies.Any(policy =>
                policy.ExpectedKind != VerifiedProcessKind.AuthBroker))
        {
            throw new ArgumentException(
                "Broker pipe requires Auth Broker client policies.",
                nameof(options));
        }
        _server = NamedPipeServerFactory.Create(
            options.PipeName,
            options.Security);
    }

    public async Task AcceptOnceAsync(
        Action<BrokerPipeConnection> connected,
        Action<BrokerPipeConnection> disconnected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connected);
        ArgumentNullException.ThrowIfNull(disconnected);
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _accepted, 1) != 0)
        {
            throw new InvalidOperationException(
                "Broker pipe server accepts only one connection.");
        }

        await _server.WaitForConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var pipeConnection = new PipeJsonConnection(_server);
        using var clientEvidence = VerifyClientProcess();
        var clientIdentity = clientEvidence.Identity;
        if (_options.RequireInteractiveSession &&
            clientIdentity.SessionId == 0)
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "broker_session_required");
        }
        using var ownerSessionLease =
            _options.OwnerSessions?.Enter(clientIdentity);

        var hello = await pipeConnection.ReadAsync<PipeHelloRequest>(
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                hello.ProtocolVersion,
                PipeProtocol.Version,
                StringComparison.Ordinal) ||
            !PipeNonce.IsValid(hello.ClientNonceB64Url))
        {
            throw new InvalidDataException("Broker hello was invalid.");
        }
        var serverNonce = PipeNonce.Create();
        await pipeConnection.WriteAsync(
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
        var authenticate = await pipeConnection.ReadAsync<PipeAuthenticateRequest>(
            cancellationToken).ConfigureAwait(false);
        var expectedTranscript = PipeHandshakeTranscript.Compute(
            NamedPipeServerFactory.NormalizePipeName(_options.PipeName),
            PipeProtocol.Version,
            hello.ClientNonceB64Url,
            serverNonce,
            clientIdentity,
            _options.ServiceIdentity);
        if (authenticate.ClientKind != VerifiedProcessKind.AuthBroker ||
            !PipeHandshakeTranscript.FixedTimeEquals(
                expectedTranscript,
                authenticate.TranscriptHashB64Url))
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "broker_handshake_mismatch");
        }
        var expiresAt = DateTimeOffset.UtcNow + _options.SessionLifetime;
        var processSessionId = Guid.NewGuid();
        await pipeConnection.WriteAsync(
            new PipeAuthenticateResponse(
                processSessionId,
                AdminSessionId: null,
                clientIdentity.UserSid,
                clientIdentity.SessionId,
                expiresAt),
            cancellationToken).ConfigureAwait(false);

        var capabilityRequest = await pipeConnection.ReadAsync<PipeRpcRequest>(
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                capabilityRequest.Method,
                "broker.capabilities",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Broker capabilities must be the first request.");
        }
        var capabilities = PipeRpcCodec.ReadPayload<BrokerCapabilitiesData>(
            capabilityRequest);
        if (!capabilities.Features.Contains(
                Capability,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Broker does not support credential prompts.");
        }
        await pipeConnection.WriteAsync(
            PipeRpcCodec.Success(
                capabilityRequest.RequestId,
                new BrokerCapabilitiesData(
                    _options.ServiceVersion,
                    [Capability])),
            cancellationToken).ConfigureAwait(false);

        var connection = new BrokerPipeConnection(
            pipeConnection,
            clientIdentity,
            capabilities,
            processSessionId,
            expiresAt,
            _options.ActivityProvider ?? new BrokerActivityProvider());
        connected(connection);
        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            disconnected(connection);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
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
            lastResult?.ReasonCode ?? "broker_identity_rejected");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class BrokerConnectionRegistry
{
    private readonly object _sync = new();
    private BrokerPipeConnection? _current;

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return _current is not null &&
                    _current.ExpiresAt > DateTimeOffset.UtcNow;
            }
        }
    }

    public void Set(BrokerPipeConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_sync)
        {
            if (_current is not null &&
                _current.ExpiresAt > DateTimeOffset.UtcNow)
            {
                throw new AdminPipeRpcException(
                    "broker_already_connected",
                    "An Auth Broker is already connected.");
            }
            _current = connection;
        }
    }

    public void Clear(BrokerPipeConnection connection)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_current, connection))
            {
                _current = null;
            }
        }
    }

    public BrokerPipeConnection GetRequired()
    {
        lock (_sync)
        {
            if (_current is null ||
                _current.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new AdminPipeRpcException(
                    "approval_broker_unavailable",
                    "The interactive Auth Broker is not available.");
            }
            return _current;
        }
    }
}
