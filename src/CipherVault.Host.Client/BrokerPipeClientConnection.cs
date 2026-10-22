using System.Text.Json;
using CipherVault.Host.Contracts;
using CipherVault.Ipc;
using CipherVault.Security.Win;

namespace CipherVault.Host.Client;

public sealed record BrokerPipeClientOptions(
    string PipeName,
    ProcessVerificationPolicy ClientPolicy,
    ProcessVerificationPolicy ServerPolicy,
    int ConnectTimeoutMilliseconds = 5_000);

public sealed class BrokerPipeClientConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private VerifiedProcessEvidence? _clientEvidence;
    private VerifiedPipeClientConnection? _verifiedConnection;
    private bool _disposed;

    private BrokerPipeClientConnection(
        VerifiedProcessEvidence clientEvidence,
        VerifiedPipeClientConnection verifiedConnection,
        BrokerCapabilitiesData capabilities,
        DateTimeOffset expiresAt)
    {
        _clientEvidence = clientEvidence;
        _verifiedConnection = verifiedConnection;
        Capabilities = capabilities;
        ExpiresAt = expiresAt;
    }

    public BrokerCapabilitiesData Capabilities { get; }
    public DateTimeOffset ExpiresAt { get; }

    public static async Task<BrokerPipeClientConnection> ConnectAsync(
        BrokerPipeClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ClientPolicy.ExpectedKind != VerifiedProcessKind.AuthBroker)
        {
            throw new ArgumentException(
                "Broker client policy must expect an Auth Broker.",
                nameof(options));
        }
        if (options.ServerPolicy.ExpectedKind != VerifiedProcessKind.HostService)
        {
            throw new ArgumentException(
                "Broker server policy must expect a Host service.",
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
                clientResult.ReasonCode ?? "broker_identity_rejected");
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
                    VerifiedProcessKind.AuthBroker,
                    transcript),
                cancellationToken).ConfigureAwait(false);
            var authentication = await verifiedConnection.Connection
                .ReadAsync<PipeAuthenticateResponse>(cancellationToken)
                .ConfigureAwait(false);
            if (authentication.ProcessSessionId == Guid.Empty ||
                authentication.AdminSessionId is not null ||
                authentication.ExpiresAt <= DateTimeOffset.UtcNow ||
                !string.Equals(
                    authentication.OwnerSid,
                    clientResult.Evidence.Identity.UserSid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Broker pipe authentication response is invalid.");
            }

            var capabilitiesRequest = PipeRpcCodec.CreateRequest(
                "broker.capabilities",
                new BrokerCapabilitiesData(
                    typeof(BrokerPipeClientConnection).Assembly
                        .GetName().Version?.ToString() ?? "1.0.0",
                    ["credential-prompt.v1"]));
            await verifiedConnection.Connection.WriteAsync(
                capabilitiesRequest,
                cancellationToken).ConfigureAwait(false);
            var capabilitiesResponse = await verifiedConnection.Connection
                .ReadAsync<PipeRpcResponse>(cancellationToken)
                .ConfigureAwait(false);
            if (capabilitiesResponse.RequestId != capabilitiesRequest.RequestId ||
                !capabilitiesResponse.Ok ||
                capabilitiesResponse.Data is null)
            {
                throw new InvalidDataException(
                    "Broker capabilities handshake failed.");
            }
            var capabilities = capabilitiesResponse.Data.Value
                .Deserialize<BrokerCapabilitiesData>(JsonOptions)
                ?? throw new InvalidDataException(
                    "Broker capabilities response was empty.");

            return new BrokerPipeClientConnection(
                clientResult.Evidence,
                verifiedConnection,
                capabilities,
                authentication.ExpiresAt);
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

    public async Task RunAsync(
        Func<
            BrokerCredentialPromptRequest,
            CancellationToken,
            Task<BrokerCredentialPromptResult>> credentialPrompt,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(
            credentialPrompt,
            approvalPrompt: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAsync(
        Func<
            BrokerCredentialPromptRequest,
            CancellationToken,
            Task<BrokerCredentialPromptResult>> credentialPrompt,
        Func<
            BrokerApprovalRequest,
            CancellationToken,
            Task<BrokerApprovalDecision>>? approvalPrompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialPrompt);
        ThrowIfDisposed();
        var connection = _verifiedConnection?.Connection
            ?? throw new ObjectDisposedException(
                nameof(BrokerPipeClientConnection));
        while (true)
        {
            PipeRpcRequest request;
            try
            {
                request = await connection.ReadAsync<PipeRpcRequest>(
                    cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            PipeRpcResponse response;
            if (!string.Equals(
                    request.ProtocolVersion,
                    PipeProtocol.Version,
                    StringComparison.Ordinal))
            {
                response = PipeRpcCodec.Failure(
                    request.RequestId,
                    "protocol_mismatch",
                    "Broker pipe protocol version is unsupported.");
            }
            else if (string.Equals(
                         request.Method,
                         "broker.credential.request",
                         StringComparison.Ordinal))
            {
                try
                {
                    var promptRequest = PipeRpcCodec
                        .ReadPayload<BrokerCredentialPromptRequest>(request);
                    var promptResult = await credentialPrompt(
                            promptRequest,
                            cancellationToken)
                        .ConfigureAwait(false);
                    response = PipeRpcCodec.Success(
                        request.RequestId,
                        promptResult);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    response = PipeRpcCodec.Failure(
                        request.RequestId,
                        "credential_prompt_failed",
                        exception.Message);
                }
            }
            else if (string.Equals(
                         request.Method,
                         "broker.approval.request",
                         StringComparison.Ordinal))
            {
                if (approvalPrompt is null)
                {
                    response = PipeRpcCodec.Failure(
                        request.RequestId,
                        "approval_unavailable",
                        "Broker approval UI is unavailable.");
                }
                else
                {
                    try
                    {
                        var approvalRequest = PipeRpcCodec
                            .ReadPayload<BrokerApprovalRequest>(request);
                        var approvalDecision = await approvalPrompt(
                                approvalRequest,
                                cancellationToken)
                            .ConfigureAwait(false);
                        response = PipeRpcCodec.Success(
                            request.RequestId,
                            approvalDecision);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        response = PipeRpcCodec.Failure(
                            request.RequestId,
                            "approval_prompt_failed",
                            exception.Message);
                    }
                }
            }
            else
            {
                response = PipeRpcCodec.Failure(
                    request.RequestId,
                    "unsupported_method",
                    $"Unsupported Broker method: {request.Method}");
            }
            await connection.WriteAsync(response, cancellationToken)
                .ConfigureAwait(false);
        }
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
            throw new InvalidDataException(
                "Broker pipe hello response is invalid.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
