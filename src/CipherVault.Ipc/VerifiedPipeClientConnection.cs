using System.IO.Pipes;
using CipherVault.Host.Contracts;
using CipherVault.Security.Win;

namespace CipherVault.Ipc;

public sealed class VerifiedPipeClientConnection : IAsyncDisposable
{
    private VerifiedProcessEvidence? _serverEvidence;
    private bool _disposed;

    private VerifiedPipeClientConnection(
        NamedPipeClientStream stream,
        PipeJsonConnection connection,
        VerifiedProcessEvidence serverEvidence)
    {
        Stream = stream;
        Connection = connection;
        _serverEvidence = serverEvidence;
    }

    public NamedPipeClientStream Stream { get; }
    public PipeJsonConnection Connection { get; }
    public VerifiedProcessIdentity ServerIdentity => _serverEvidence?.Identity
        ?? throw new ObjectDisposedException(nameof(VerifiedPipeClientConnection));

    public static async Task<VerifiedPipeClientConnection> ConnectAsync(
        string pipeName,
        ProcessVerificationPolicy serverPolicy,
        int connectTimeoutMilliseconds = 5_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(serverPolicy);
        if (serverPolicy.ExpectedKind != VerifiedProcessKind.HostService)
        {
            throw new ArgumentException(
                "Server verification policy must expect a Host service.",
                nameof(serverPolicy));
        }

        var normalizedName = NamedPipeServerFactory.NormalizePipeName(pipeName);
        var stream = new NamedPipeClientStream(
            ".",
            normalizedName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(
                connectTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            var serverPid = NamedPipeProcessIdentity.GetServerProcessId(
                stream.SafePipeHandle);
            var verification = WindowsProcessIdentityVerifier.Verify(
                serverPid,
                serverPolicy);
            if (verification.Status != ProcessVerificationStatus.Verified ||
                verification.Evidence is null)
            {
                verification.Evidence?.Dispose();
                throw new ProcessIdentityRejectedException(
                    verification.Status,
                    verification.ReasonCode ?? "server_identity_rejected");
            }
            return new VerifiedPipeClientConnection(
                stream,
                new PipeJsonConnection(stream),
                verification.Evidence);
        }
        catch
        {
            stream.Dispose();
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
        await Connection.DisposeAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _serverEvidence, null)?.Dispose();
    }
}

public sealed class ProcessIdentityRejectedException : Exception
{
    public ProcessIdentityRejectedException(
        ProcessVerificationStatus status,
        string reasonCode)
        : base($"Process identity verification failed: {reasonCode}")
    {
        Status = status;
        ReasonCode = reasonCode;
    }

    public ProcessVerificationStatus Status { get; }
    public string ReasonCode { get; }
}
