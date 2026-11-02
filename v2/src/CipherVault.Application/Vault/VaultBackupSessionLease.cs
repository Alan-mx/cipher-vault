using CipherVault.Core.Backup;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Vault;

internal sealed class VaultBackupSessionLease : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly VaultSessionManager manager;
    private readonly VaultSession session;
    private readonly CancellationToken requestToken;
    private readonly CancellationToken sessionToken;
    private readonly CancellationTokenSource linkedCancellation;
    private int activeCredentialCount;
    private bool keyAccessSealed;
    private bool disposed;
    private bool publishCommitGranted;
    private Exception? cleanupFailure;

    internal VaultBackupSessionLease(
        VaultSessionManager manager,
        VaultSession session,
        CancellationToken requestToken)
    {
        this.manager = manager;
        this.session = session;
        this.requestToken = requestToken;
        sessionToken = session.OperationCancellation.Token;
        linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            requestToken,
            sessionToken);
    }

    internal Guid SessionId => session.SessionId;

    internal long SessionGeneration => session.Generation;

    internal VaultSession Session => session;

    internal bool IsRequestCancellationRequested =>
        requestToken.IsCancellationRequested;

    internal bool IsLockCancellationRequested =>
        sessionToken.IsCancellationRequested;

    internal CancellationToken CancellationToken =>
        linkedCancellation.Token;

    internal int ActiveCredentialCountForTest
    {
        get
        {
            lock (sync)
            {
                return activeCredentialCount;
            }
        }
    }

    internal bool IsPublishCommitGranted
    {
        get
        {
            lock (sync)
            {
                return publishCommitGranted;
            }
        }
    }

    internal SnapshotDatabaseKeyCredential CreateDatabaseKeyCredential()
    {
        ThrowIfCancellationRequested();
        SnapshotDatabaseKeyCredential? credential = null;
        var cancellationRequested = false;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (requestToken.IsCancellationRequested
                || sessionToken.IsCancellationRequested)
            {
                cancellationRequested = true;
            }
            else if (keyAccessSealed || activeCredentialCount != 0)
            {
                throw new VaultException(
                    VaultErrorCode.VaultBusy,
                    "Backup database key access is not available.");
            }
            else
            {
                activeCredentialCount = 1;
                try
                {
                    credential = new SnapshotDatabaseKeyCredential(
                        session.KeyMaterial.DatabaseKey,
                        OnCredentialDisposed);
                }
                catch
                {
                    activeCredentialCount = 0;
                    throw;
                }
            }
        }

        if (cancellationRequested)
        {
            ThrowIfCancellationRequested();
            throw new OperationCanceledException();
        }

        return credential!;
    }

    internal bool TryBeginPublishCommit() =>
        manager.TryBeginPublishCommit(this);

    internal bool TryBeginPublishCommitCore()
    {
        lock (sync)
        {
            if (disposed
                || !keyAccessSealed
                || activeCredentialCount != 0
                || publishCommitGranted
                || requestToken.IsCancellationRequested
                || sessionToken.IsCancellationRequested)
            {
                return false;
            }

            publishCommitGranted = true;
            return true;
        }
    }

    internal void SealDatabaseKeyAccess()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeCredentialCount != 0)
            {
                throw new VaultException(
                    VaultErrorCode.VaultBusy,
                    "Backup database key credential is still active.");
            }

            keyAccessSealed = true;
        }
    }

    internal void MarkCleanupFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (sync)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(cleanupFailure, exception);
        }
    }

    internal void ThrowIfCancellationRequested()
    {
        if (sessionToken.IsCancellationRequested)
        {
            throw manager.CreateBackupCancellationException(session);
        }

        requestToken.ThrowIfCancellationRequested();
    }

    internal void EnsureMatches(VaultSession current)
    {
        if (!ReferenceEquals(session, current))
        {
            throw new VaultException(
                VaultErrorCode.VaultLocked,
                "Backup lease does not belong to the active Vault Session.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }

            if (!keyAccessSealed)
            {
                throw new VaultException(
                    VaultErrorCode.VaultBusy,
                    "Backup key access must be sealed before lease disposal.");
            }

            if (activeCredentialCount != 0)
            {
                throw new VaultException(
                    VaultErrorCode.VaultBusy,
                    "Backup lease cannot dispose while a credential is active.");
            }

            disposed = true;
        }

        session.CompleteBackupOperation(cleanupFailure is null);
        linkedCancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnCredentialDisposed()
    {
        lock (sync)
        {
            if (activeCredentialCount > 0)
            {
                activeCredentialCount--;
            }
        }
    }
}
