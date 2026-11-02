using CipherVault.Application.Backup;
using CipherVault.Core.Backup;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Application.Vault;

public sealed class VaultSessionManager : IAsyncDisposable
{
    private readonly object transitionSync = new();
    private readonly VaultInstancePaths paths;
    private readonly VaultSessionOptions options;
    private readonly VaultSessionTimingOptions timingOptions;
    private readonly TimeProvider timeProvider;
    private readonly object disposeSync = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly AsyncLocal<bool> operationDepth = new();

    public event Action<VaultStateChangedEvent>? StateChanged;
    private int state;
    private VaultSession? session = null;
    private VaultLockReason lockReason = VaultLockReason.None;
    private Task<VaultSessionStatus>? activeLockTask = null;
    private TaskCompletionSource? activeTransitionCompletion = null;
    private long nextSessionGeneration;
    private Task? disposeTask;
    private int disposed;

    internal VaultSessionManager(
        VaultInstancePaths paths,
        VaultSessionOptions options,
        VaultSessionTimingOptions timingOptions,
        TimeProvider timeProvider,
        VaultSessionState initialState)
    {
        this.paths = paths;
        this.options = options;
        this.timingOptions = timingOptions;
        this.timeProvider = timeProvider;
        state = (int)initialState;
    }

    public static async Task<VaultSessionManager> CreateAsync(
        string vaultDirectory,
        VaultSessionOptions? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        return await CreateCoreAsync(
            vaultDirectory,
            options,
            VaultSessionTimingOptions.Default,
            timeProvider,
            cancellationToken);
    }

    internal static Task<VaultSessionManager> CreateForTestAsync(
        string vaultDirectory,
        VaultSessionOptions? options,
        VaultSessionTimingOptions timingOptions,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken = default) =>
        CreateCoreAsync(
            vaultDirectory,
            options,
            timingOptions,
            timeProvider,
            cancellationToken);

    private static async Task<VaultSessionManager> CreateCoreAsync(
        string vaultDirectory,
        VaultSessionOptions? options,
        VaultSessionTimingOptions timingOptions,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken)
    {
        var paths = VaultInstancePaths.Create(vaultDirectory);
        var layout = await ProbeLayoutAsync(paths, cancellationToken);
        var initialState = layout switch
        {
            VaultInstanceLayout.Absent => VaultSessionState.Uninitialized,
            VaultInstanceLayout.Initialized => VaultSessionState.Locked,
            _ => throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Vault layout is incomplete or invalid.")
        };

        return new VaultSessionManager(
            paths,
            options ?? VaultSessionOptions.Default,
            timingOptions,
            timeProvider ?? TimeProvider.System,
            initialState);
    }

    public async Task<VaultSessionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        await CheckIdleAndLockAsync(cancellationToken);

    internal async Task<VaultSessionStatus> EnsureUnlockedWithoutActivityAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if ((VaultSessionState)state != VaultSessionState.Unlocked)
        {
            throw CreateBusinessStateException((VaultSessionState)state);
        }

        return status;
    }

    internal Task<VaultBackupSessionLease> BeginBackupSessionAsync(
        CancellationToken requestToken = default)
    {
        ThrowIfDisposed();
        requestToken.ThrowIfCancellationRequested();
        lock (transitionSync)
        {
            if ((VaultSessionState)state != VaultSessionState.Unlocked
                || session is null)
            {
                throw CreateBusinessStateException((VaultSessionState)state);
            }

            if (session.OperationCancellation.IsCancellationRequested)
            {
                throw new VaultException(
                    VaultErrorCode.VaultLocked,
                    "Vault locked before backup activity could begin.");
            }

            var current = session;
            current.RegisterBackupOperation();
            try
            {
                return Task.FromResult(
                    new VaultBackupSessionLease(
                        this,
                        current,
                        requestToken));
            }
            catch
            {
                current.CompleteBackupOperation(cleanupSucceeded: true);
                throw;
            }
        }
    }

    internal bool TryBeginPublishCommit(VaultBackupSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (transitionSync)
        {
            if ((VaultSessionState)state != VaultSessionState.Unlocked
                || session is null
                || !ReferenceEquals(session, lease.Session))
            {
                return false;
            }

            return lease.TryBeginPublishCommitCore();
        }
    }

    internal async ValueTask<T> ExecuteDownloadAdmissionAsync<T>(
        Func<VaultOperationContext, CancellationToken, ValueTask<T>> operation,
        CancellationToken requestToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        requestToken.ThrowIfCancellationRequested();
        VaultSession current;
        lock (transitionSync)
        {
            if ((VaultSessionState)state != VaultSessionState.Unlocked
                || session is null)
            {
                throw CreateBusinessStateException((VaultSessionState)state);
            }

            current = session;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            requestToken,
            current.OperationCancellation.Token);
        await operationGate.WaitAsync(cancellation.Token);
        try
        {
            lock (transitionSync)
            {
                if ((VaultSessionState)state != VaultSessionState.Unlocked
                    || !ReferenceEquals(session, current))
                {
                    throw new VaultException(
                        VaultErrorCode.VaultLocked,
                        "Vault locked before download admission.");
                }

                current.Activity.Refresh();
            }

            var context = current.Database.CreateOperationContext();
            try
            {
                return await operation(context, requestToken);
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<bool> TryExecuteAuditNoActivityAsync(
        Func<VaultOperationContext, CancellationToken, ValueTask> operation,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var gateHeld = false;
        try
        {
            await operationGate.WaitAsync(cancellation.Token);
            gateHeld = true;
            VaultSession current;
            lock (transitionSync)
            {
                if ((VaultSessionState)state != VaultSessionState.Unlocked
                    || session is null)
                {
                    return false;
                }

                current = session;
            }

            var context = current.Database.CreateOperationContext();
            try
            {
                await operation(context, cancellation.Token);
                return true;
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (gateHeld)
            {
                operationGate.Release();
            }
        }
    }

    internal VaultException CreateBackupCancellationException(
        VaultSession canceledSession)
    {
        lock (transitionSync)
        {
            return (VaultSessionState)state == VaultSessionState.LockFailed
                ? new VaultException(
                    VaultErrorCode.VaultLockFailed,
                    "The Vault lock failed while backup activity was running.")
                : new VaultException(
                    VaultErrorCode.VaultLocked,
                    ReferenceEquals(session, canceledSession)
                        ? "The Vault locked while backup activity was running."
                        : "The Vault locked while the backup Session was active.");
        }
    }

    internal async ValueTask<BackupSnapshotCapture>
        CaptureBackupSnapshotAsync(
            VaultBackupSessionLease backupLease,
            string workingDirectory,
            DateTimeOffset capturedAtUtc) =>
        await ExecuteAsync(
            async (context, token) =>
            {
                ArgumentNullException.ThrowIfNull(backupLease);
                var current = session
                    ?? throw new VaultException(
                        VaultErrorCode.VaultLocked,
                        "Vault locked before the backup snapshot could start.");
                backupLease.EnsureMatches(current);
                backupLease.ThrowIfCancellationRequested();
                var directory = PathSafety.Canonicalize(
                    workingDirectory,
                    "Backup working directory");
                Directory.CreateDirectory(directory);
                VaultFilePermissions.ProtectDirectory(directory);
                PathSafety.EnsureNoLinkComponents(directory);
                var databasePath = Path.Combine(
                    directory,
                    BackupConstants.DatabaseEntryName);
                var keyPath = Path.Combine(
                    directory,
                    BackupConstants.KeyEnvelopeEntryName);
                if (File.Exists(databasePath) || File.Exists(keyPath))
                {
                    throw new VaultException(
                        VaultErrorCode.StorageError,
                        "Backup working directory must be empty.");
                }

                var databaseKey = current.KeyMaterial.DatabaseKey;
                await BackupDatabaseStore.CreateEncryptedSnapshotAsync(
                    context,
                    directory,
                    databaseKey,
                    token);
                backupLease.ThrowIfCancellationRequested();
                await CopyProtectedFileAsync(
                    paths.KeyPath,
                    keyPath,
                    token);
                backupLease.ThrowIfCancellationRequested();
                var envelope = await VaultKeyEnvelopeSerializer.LoadAsync(
                    keyPath,
                    token);
                if (envelope.VaultId != context.VaultId)
                {
                    throw new VaultException(
                        VaultErrorCode.CorruptVault,
                        "Vault key does not belong to the backup snapshot.");
                }
                var databaseInfo = new FileInfo(databasePath);
                var keyInfo = new FileInfo(keyPath);
                return new BackupSnapshotCapture(
                    context.VaultId,
                    capturedAtUtc.ToUniversalTime(),
                    directory,
                    databasePath,
                    keyPath,
                    databaseInfo.Length,
                    keyInfo.Length);
            },
            backupLease.CancellationToken).AsTask();

    public async Task<VaultSessionStatus> InitializeAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        PasswordPolicy.Validate(password);
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource? transitionCompletion = null;
        lock (transitionSync)
        {
            switch ((VaultSessionState)state)
            {
                case VaultSessionState.Uninitialized:
                    transitionCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    activeTransitionCompletion = transitionCompletion;
                    state = (int)VaultSessionState.Initializing;
                    break;
                case VaultSessionState.Initializing:
                case VaultSessionState.Unlocking:
                case VaultSessionState.Locking:
                    throw new VaultException(
                        VaultErrorCode.VaultBusy,
                        "A Vault transition is already in progress.");
                case VaultSessionState.Locked:
                case VaultSessionState.Unlocked:
                    throw new VaultException(
                        VaultErrorCode.VaultAlreadyInitialized,
                        "The Vault is already initialized.");
                default:
                    throw new VaultException(
                        VaultErrorCode.VaultLockFailed,
                        "The Vault requires a process restart.");
            }
        }

        VaultInitializationScope? scope = null;
        VaultKeyMaterial? keyMaterial = null;
        var installed = false;
        try
        {
            var parent = Path.GetDirectoryName(paths.VaultDirectory)
                ?? throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Vault directory has no parent.");
            Directory.CreateDirectory(parent);
            EnsureInstallTargetAvailable(requireAbsentOrEmpty: true);

            var stagingPath = CreateStagingPath(parent);
            Directory.CreateDirectory(stagingPath);
            scope = new VaultInitializationScope(paths, stagingPath);
            VaultFilePermissions.ProtectDirectory(stagingPath);

            var creation = VaultKeyService.Create(password, Guid.NewGuid());
            keyMaterial = creation.KeyMaterial;
            await VaultKeyEnvelopeSerializer.WriteAtomicAsync(
                Path.Combine(stagingPath, VaultInstancePaths.KeyFileName),
                creation.Envelope,
                cancellationToken);
            await SqliteMcVaultStore.InitializeAsync(
                stagingPath,
                creation.KeyMaterial,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            EnsureInstallTargetAvailable(requireAbsentOrEmpty: true);
            if (Directory.Exists(paths.VaultDirectory))
            {
                Directory.Delete(paths.VaultDirectory, recursive: false);
            }

            Directory.Move(stagingPath, paths.VaultDirectory);
            installed = true;
            scope.MarkSucceeded();

            lock (transitionSync)
            {
                session = null;
                lockReason = VaultLockReason.None;
                state = (int)VaultSessionState.Locked;
                return CreateStatus();
            }
        }
        catch (Exception exception)
        {
            VaultException failure;
            if (scope is not null && !installed)
            {
                try
                {
                    scope.Cleanup();
                }
                catch (VaultException cleanupException)
                {
                    failure = cleanupException;
                    ResetInitializationState();
                    throw failure;
                }
            }

            ResetInitializationState();
            if (exception is OperationCanceledException)
            {
                throw;
            }

            if (exception is VaultException vaultException)
            {
                throw vaultException;
            }

            failure = new VaultException(
                VaultErrorCode.StorageError,
                "Vault initialization failed.");
            throw failure;
        }
        finally
        {
            CompleteTransition(transitionCompletion);
            keyMaterial?.Dispose();
        }
    }

    public async Task<VaultSessionStatus> UnlockAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource? transitionCompletion = null;
        lock (transitionSync)
        {
            switch ((VaultSessionState)state)
            {
                case VaultSessionState.Unlocked:
                    return CreateStatus();
                case VaultSessionState.Locked:
                    transitionCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    activeTransitionCompletion = transitionCompletion;
                    state = (int)VaultSessionState.Unlocking;
                    break;
                case VaultSessionState.Initializing:
                case VaultSessionState.Unlocking:
                case VaultSessionState.Locking:
                    throw new VaultException(
                        VaultErrorCode.VaultBusy,
                        "A Vault transition is already in progress.");
                case VaultSessionState.Uninitialized:
                    throw new VaultException(
                        VaultErrorCode.VaultNotInitialized,
                        "The Vault has not been initialized.");
                default:
                    throw new VaultException(
                        VaultErrorCode.VaultLockFailed,
                        "The Vault requires a process restart.");
            }
        }

        VaultKeyMaterial? keyMaterial = null;
        VaultDatabaseRuntime? runtime = null;
        var ownershipTransferred = false;
        try
        {
            keyMaterial = await VaultKeyService.UnlockAsync(
                paths.KeyPath,
                password,
                cancellationToken);
            var sessionId = Guid.NewGuid();
            var generation = Interlocked.Increment(ref nextSessionGeneration);
            runtime = await VaultDatabaseRuntime.OpenAsync(
                paths.VaultDirectory,
                keyMaterial,
                sessionId,
                generation,
                cancellationToken);
            var unlockedSession = new VaultSession(
                sessionId,
                generation,
                runtime,
                keyMaterial,
                timeProvider.GetUtcNow(),
                new VaultActivityState(timeProvider));

            VaultSessionStatus completedStatus;
            lock (transitionSync)
            {
                if ((VaultSessionState)state != VaultSessionState.Unlocking)
                {
                    throw new VaultException(
                        VaultErrorCode.VaultBusy,
                        "Vault unlock was superseded by another transition.");
                }

                session = unlockedSession;
                lockReason = VaultLockReason.None;
                state = (int)VaultSessionState.Unlocked;
                ownershipTransferred = true;
                completedStatus = CreateStatus();
            }

            RaiseStateChanged(completedStatus);
            return completedStatus;
        }
        catch (Exception exception)
        {
            if (runtime is not null && !ownershipTransferred)
            {
                try
                {
                    await runtime.DisposeAsync();
                }
                catch
                {
                    // Preserve the original unlock failure.
                }
            }

            ResetUnlockState();
            if (exception is OperationCanceledException)
            {
                throw;
            }

            if (exception is VaultException vaultException)
            {
                throw vaultException;
            }

            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Vault unlock failed.");
        }
        finally
        {
            CompleteTransition(transitionCompletion);
            if (!ownershipTransferred)
            {
                keyMaterial?.Dispose();
            }
        }
    }

    public Task<VaultSessionStatus> LockAsync(
        CancellationToken cancellationToken = default) =>
        LockCoreAsync(VaultLockReason.UserRequested, cancellationToken);

    internal async Task<VaultSessionStatus> ChangePasswordAndLockAsync(
        string oldPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Task<VaultSessionStatus> lockTask;
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            lock (transitionSync)
            {
                if ((VaultSessionState)state != VaultSessionState.Unlocked)
                {
                    throw CreateBusinessStateException((VaultSessionState)state);
                }
            }

            var changed = await VaultKeyService.ChangePasswordAsync(
                paths.KeyPath,
                oldPassword,
                newPassword,
                cancellationToken);
            changed.KeyMaterial.Dispose();
            if (PasswordChangeLockTransitionBarrierForTest is { } barrier)
            {
                await barrier();
            }

            lockTask = BeginLockTransition(VaultLockReason.UserRequested);
        }
        finally
        {
            operationGate.Release();
        }

        return await lockTask.WaitAsync(CancellationToken.None);
    }

    public async ValueTask TouchActivityAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        VaultSession current;
        bool idleExpired;
        lock (transitionSync)
        {
            if ((VaultSessionState)state != VaultSessionState.Unlocked
                || session is null)
            {
                throw CreateBusinessStateException((VaultSessionState)state);
            }

            current = session;
            idleExpired = IsIdleExpired(current);
            if (!idleExpired)
            {
                current.Activity.Refresh();
                return;
            }
        }

        var locked = await LockCoreAsync(
            VaultLockReason.IdleTimeout,
            cancellationToken);
        if (locked.State == VaultSessionState.LockFailed)
        {
            throw new VaultException(
                VaultErrorCode.VaultLockFailed,
                "Vault idle lock failed.");
        }

        throw new VaultException(
            VaultErrorCode.VaultLocked,
            "Vault idle timeout has expired.");
    }

    public async Task<VaultSessionStatus> CheckIdleAndLockAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Task<VaultSessionStatus>? existingLockTask = null;
        lock (transitionSync)
        {
            var currentState = (VaultSessionState)state;
            if (currentState == VaultSessionState.Locking
                && activeLockTask is not null)
            {
                existingLockTask = activeLockTask;
            }
            else if (currentState != VaultSessionState.Unlocked
                || session is null)
            {
                return CreateStatus();
            }

            if (existingLockTask is null && !IsIdleExpired(session!))
            {
                return CreateStatus();
            }
        }

        if (existingLockTask is not null)
        {
            return await existingLockTask.WaitAsync(cancellationToken);
        }

        return await LockCoreAsync(VaultLockReason.IdleTimeout, cancellationToken);
    }

    public async ValueTask<T> ExecuteAsync<T>(
        Func<VaultOperationContext, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        if (operationDepth.Value)
        {
            throw new VaultException(
                VaultErrorCode.VaultBusy,
                "Vault operations cannot be nested.");
        }

        VaultSession current;
        bool idleExpired;
        lock (transitionSync)
        {
            if ((VaultSessionState)state != VaultSessionState.Unlocked
                || session is null)
            {
                throw CreateBusinessStateException((VaultSessionState)state);
            }

            current = session;
            idleExpired = IsIdleExpired(current);
        }

        if (idleExpired)
        {
            throw await CreateIdleLockExceptionAsync(cancellationToken);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            current.OperationCancellation.Token);
        var gateHeld = false;
        var operationStarted = false;
        var idleLockRequired = false;
        T result = default!;
        try
        {
            await operationGate.WaitAsync(linkedCancellation.Token);
            gateHeld = true;
            lock (transitionSync)
            {
                if ((VaultSessionState)state != VaultSessionState.Unlocked
                    || !ReferenceEquals(session, current))
                {
                    throw new VaultException(
                        VaultErrorCode.VaultLocked,
                        "The Vault locked before the operation could start.");
                }

                idleLockRequired = IsIdleExpired(current);
            }

            if (!idleLockRequired)
            {
                current.Activity.Refresh();
                operationDepth.Value = true;
                operationStarted = true;
                result = await current.Database.ExecuteAsync(
                    operation,
                    linkedCancellation.Token);

                lock (transitionSync)
                {
                    if ((VaultSessionState)state == VaultSessionState.LockFailed)
                    {
                        throw new VaultException(
                            VaultErrorCode.VaultLockFailed,
                            "The Vault lock failed while the operation was running.");
                    }

                    if ((VaultSessionState)state != VaultSessionState.Unlocked
                        || !ReferenceEquals(session, current))
                    {
                        throw new VaultException(
                            VaultErrorCode.VaultLocked,
                            "The Vault locked while the operation was running.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && current.OperationCancellation.IsCancellationRequested)
        {
            throw CreateLockCancellationException();
        }
        finally
        {
            if (operationStarted)
            {
                operationDepth.Value = false;
            }

            if (gateHeld)
            {
                operationGate.Release();
            }
        }

        if (idleLockRequired)
        {
            throw await CreateIdleLockExceptionAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (disposeSync)
        {
            if (disposeTask is null)
            {
                Volatile.Write(ref disposed, 1);
                disposeTask = DisposeCoreAsync();
            }

            task = disposeTask;
        }

        return new ValueTask(task);
    }

    internal VaultInstancePaths Paths => paths;

    internal VaultSessionOptions Options => options;

    internal TimeProvider TimeProvider => timeProvider;

    internal VaultSessionTimingOptions TimingOptions => timingOptions;

    internal Func<Task>? LockCleanupBarrierForTest { get; set; }

    internal Func<Task>? PasswordChangeLockTransitionBarrierForTest { get; set; }

    internal VaultSessionState State =>
        (VaultSessionState)Volatile.Read(ref state);

    internal VaultSession? Session
    {
        get
        {
            lock (transitionSync)
            {
                return session;
            }
        }
    }

    private static async Task<VaultInstanceLayout> ProbeLayoutAsync(
        VaultInstancePaths paths,
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(paths.VaultDirectory))
            {
                return VaultInstanceLayout.InvalidExistingDirectory;
            }

            var directoryExists = Directory.Exists(paths.VaultDirectory);
            var keyExists = File.Exists(paths.KeyPath);
            var databaseExists = File.Exists(paths.DatabasePath);
            if (keyExists != databaseExists)
            {
                return VaultInstanceLayout.Partial;
            }

            if (keyExists)
            {
                try
                {
                    _ = await VaultKeyEnvelopeSerializer.LoadAsync(
                        paths.KeyPath,
                        cancellationToken);
                    return VaultInstanceLayout.Initialized;
                }
                catch (VaultException exception) when (
                    exception.Code is VaultErrorCode.CorruptVault
                        or VaultErrorCode.UnsupportedVersion)
                {
                    return VaultInstanceLayout.CorruptEnvelope;
                }
            }

            if (!directoryExists)
            {
                return VaultInstanceLayout.Absent;
            }

            using var entries = Directory.EnumerateFileSystemEntries(paths.VaultDirectory)
                .GetEnumerator();
            return entries.MoveNext()
                ? VaultInstanceLayout.InvalidExistingDirectory
                : VaultInstanceLayout.Absent;
        }
        catch (VaultException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to inspect the Vault directory.");
        }
    }

    private async Task<VaultSessionStatus> LockCoreAsync(
        VaultLockReason reason,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await LockCoreInternalAsync(reason, cancellationToken);
    }

    private async Task<VaultSessionStatus> LockCoreInternalAsync(
        VaultLockReason reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = BeginLockTransition(reason);
        return await task.WaitAsync(cancellationToken);
    }

    private Task<VaultSessionStatus> BeginLockTransition(
        VaultLockReason reason)
    {
        lock (transitionSync)
        {
            switch ((VaultSessionState)state)
            {
                case VaultSessionState.Locked:
                    return Task.FromResult(CreateStatus());
                case VaultSessionState.Locking:
                    return activeLockTask
                           ?? throw new VaultException(
                            VaultErrorCode.VaultBusy,
                            "The Vault lock task is unavailable.");
                case VaultSessionState.Unlocked:
                    var current = session
                                  ?? throw new VaultException(
                                      VaultErrorCode.VaultBusy,
                                      "The unlocked Vault has no active session.");
                    var completion = new TaskCompletionSource<VaultSessionStatus>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    activeLockTask = completion.Task;
                    lockReason = reason;
                    state = (int)VaultSessionState.Locking;
                    _ = Task.Run(
                        () => RunLockCleanupAsync(current, completion),
                        CancellationToken.None);
                    return completion.Task;
                case VaultSessionState.Initializing:
                case VaultSessionState.Unlocking:
                    throw new VaultException(
                        VaultErrorCode.VaultBusy,
                        "A Vault transition is already in progress.");
                case VaultSessionState.Uninitialized:
                    throw new VaultException(
                        VaultErrorCode.VaultNotInitialized,
                        "The Vault has not been initialized.");
                default:
                    throw new VaultException(
                        VaultErrorCode.VaultLockFailed,
                        "The Vault requires a process restart.");
            }
        }
    }

    private async Task RunLockCleanupAsync(
        VaultSession lockedSession,
        TaskCompletionSource<VaultSessionStatus> completion)
    {
        var startedAt = timeProvider.GetTimestamp();
        var gateAcquired = false;
        var backupDrained = false;
        try
        {
            var barrier = LockCleanupBarrierForTest;
            if (barrier is not null)
            {
                await barrier();
            }

            lockedSession.OperationCancellation.Cancel();
            backupDrained = await WaitForBackupDrainAsync(
                lockedSession,
                GetRemainingTime(startedAt, timingOptions.CancelGrace));
            if (backupDrained)
            {
                gateAcquired = await TryAcquireOperationGateAsync(
                    GetRemainingTime(startedAt, timingOptions.CancelGrace));
            }

            if (!backupDrained || !gateAcquired)
            {
                lockedSession.Database.TryInterruptActiveCommand();
                if (!backupDrained)
                {
                    backupDrained = await WaitForBackupDrainAsync(
                        lockedSession,
                        GetRemainingTime(
                            startedAt,
                            timingOptions.TotalLockDeadline));
                }

                if (backupDrained && !gateAcquired)
                {
                    gateAcquired = await TryAcquireOperationGateAsync(
                        GetRemainingTime(
                            startedAt,
                            timingOptions.TotalLockDeadline));
                }
            }

            if (!backupDrained || !gateAcquired)
            {
                SetLockFailed();
                var failedStatus = CreateStatus();
                RaiseStateChanged(failedStatus);
                completion.TrySetResult(failedStatus);
                return;
            }

            var cleanupFailed = lockedSession.BackupCleanupFailed;
            await lockedSession.ReleaseAsync();
            VaultSessionStatus lockedStatus;
            lock (transitionSync)
            {
                if (ReferenceEquals(session, lockedSession))
                {
                    session = null;
                }

                state = cleanupFailed
                    ? (int)VaultSessionState.LockFailed
                    : (int)VaultSessionState.Locked;
                lockedStatus = CreateStatus();
            }

            RaiseStateChanged(lockedStatus);
            completion.TrySetResult(lockedStatus);
        }
        catch
        {
            SetLockFailed();
            var failedStatus = CreateStatus();
            RaiseStateChanged(failedStatus);
            completion.TrySetResult(failedStatus);
        }
        finally
        {
            if (gateAcquired)
            {
                operationGate.Release();
            }
        }
    }

    private async Task<bool> TryAcquireOperationGateAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return operationGate.Wait(0);
        }

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        try
        {
            await operationGate.WaitAsync(timeoutCancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (
            timeoutCancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> WaitForBackupDrainAsync(
        VaultSession current,
        TimeSpan timeout)
    {
        var drained = current.BackupOperationsDrainedTask;
        if (drained.IsCompleted)
        {
            return true;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            await drained.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private TimeSpan GetRemainingTime(
        long startedAt,
        TimeSpan deadline)
    {
        var elapsed = timeProvider.GetElapsedTime(
            startedAt,
            timeProvider.GetTimestamp());
        return elapsed >= deadline
            ? TimeSpan.Zero
            : deadline - elapsed;
    }

    private void SetLockFailed()
    {
        lock (transitionSync)
        {
            state = (int)VaultSessionState.LockFailed;
        }
    }

    private void RaiseStateChanged(VaultSessionStatus status)
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        var change = new VaultStateChangedEvent(
            status,
            timeProvider.GetUtcNow());
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<VaultStateChangedEvent>)handler)(change);
            }
            catch
            {
                // Observers must not change Vault lifecycle behavior.
            }
        }
    }

    private bool IsIdleExpired(VaultSession current)
    {
        var activity = current.Activity.Snapshot();
        var elapsed = timeProvider.GetElapsedTime(
            activity.MonotonicTimestamp,
            timeProvider.GetTimestamp());
        return elapsed >= options.IdleTimeout;
    }

    private async Task<VaultException> CreateIdleLockExceptionAsync(
        CancellationToken cancellationToken)
    {
        var status = await LockCoreAsync(
            VaultLockReason.IdleTimeout,
            cancellationToken);
        return status.State == VaultSessionState.LockFailed
            ? new VaultException(
                VaultErrorCode.VaultLockFailed,
                "Vault idle lock failed.")
            : new VaultException(
                VaultErrorCode.VaultLocked,
                "Vault idle timeout has expired.");
    }

    private VaultException CreateLockCancellationException()
    {
        lock (transitionSync)
        {
            return (VaultSessionState)state == VaultSessionState.LockFailed
                ? new VaultException(
                    VaultErrorCode.VaultLockFailed,
                    "The Vault lock failed while the operation was running.")
                : new VaultException(
                    VaultErrorCode.VaultLocked,
                    "The Vault locked while the operation was running.");
        }
    }

    private void EnsureInstallTargetAvailable(bool requireAbsentOrEmpty)
    {
        if (File.Exists(paths.VaultDirectory))
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Vault target is not a directory.");
        }

        if (!Directory.Exists(paths.VaultDirectory))
        {
            return;
        }

        if (!requireAbsentOrEmpty
            || Directory.EnumerateFileSystemEntries(paths.VaultDirectory).Any())
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Vault target directory is not empty.");
        }
    }

    private static string CreateStagingPath(string parent)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var path = Path.Combine(
                parent,
                $".cipher-vault-init-{Guid.NewGuid():N}");
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                return path;
            }
        }

        throw new VaultException(
            VaultErrorCode.StorageError,
            "Unable to allocate a Vault initialization staging directory.");
    }

    private void ResetInitializationState()
    {
        lock (transitionSync)
        {
            if ((VaultSessionState)state == VaultSessionState.Initializing)
            {
                state = (int)VaultSessionState.Uninitialized;
            }
        }
    }

    private void ResetUnlockState()
    {
        lock (transitionSync)
        {
            if ((VaultSessionState)state == VaultSessionState.Unlocking)
            {
                state = (int)VaultSessionState.Locked;
            }
        }
    }

    private void CompleteTransition(TaskCompletionSource? completion)
    {
        if (completion is null)
        {
            return;
        }

        lock (transitionSync)
        {
            if (ReferenceEquals(activeTransitionCompletion, completion))
            {
                activeTransitionCompletion = null;
            }
        }

        completion.TrySetResult();
    }

    private async Task DisposeCoreAsync()
    {
        var disposeGate = false;
        try
        {
            while (true)
            {
                VaultSessionState currentState;
                Task<VaultSessionStatus>? currentLockTask;
                TaskCompletionSource? transitionCompletion;
                lock (transitionSync)
                {
                    currentState = (VaultSessionState)state;
                    currentLockTask = activeLockTask;
                    transitionCompletion = activeTransitionCompletion;
                }

                switch (currentState)
                {
                    case VaultSessionState.Unlocked:
                        _ = await LockCoreInternalAsync(
                            VaultLockReason.ProcessExit,
                            CancellationToken.None);
                        continue;
                    case VaultSessionState.Locking:
                        if (currentLockTask is not null)
                        {
                            _ = await currentLockTask;
                            continue;
                        }

                        break;
                    case VaultSessionState.Initializing:
                    case VaultSessionState.Unlocking:
                        if (transitionCompletion is not null)
                        {
                            await transitionCompletion.Task;
                        }
                        else
                        {
                            await Task.Delay(10);
                        }

                        continue;
                    case VaultSessionState.LockFailed:
                        disposeGate = await TryReleaseFailedSessionAsync();
                        break;
                    default:
                        disposeGate = true;
                        break;
                }

                break;
            }
        }
        finally
        {
            if (disposeGate)
            {
                operationGate.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }

    private async Task<bool> TryReleaseFailedSessionAsync()
    {
        VaultSession? current;
        lock (transitionSync)
        {
            current = session;
        }

        if (current is null)
        {
            return true;
        }

        if (current.HasActiveBackupOperationsForTest)
        {
            return false;
        }

        if (!operationGate.Wait(TimeSpan.FromMilliseconds(250)))
        {
            return false;
        }

        try
        {
            current.OperationCancellation.Cancel();
            var cleanupFailed = current.BackupCleanupFailed;
            await current.ReleaseAsync();
            lock (transitionSync)
            {
                if (ReferenceEquals(session, current))
                {
                    session = null;
                }

                state = cleanupFailed
                    ? (int)VaultSessionState.LockFailed
                    : (int)VaultSessionState.Locked;
            }

            RaiseStateChanged(CreateStatus());
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private VaultSessionStatus CreateStatus()
    {
        var currentState = (VaultSessionState)state;
        if (session is null
            || currentState is VaultSessionState.Uninitialized
                or VaultSessionState.Locked
                or VaultSessionState.LockFailed)
        {
            return new VaultSessionStatus(
                currentState,
                null,
                null,
                null,
                null,
                (int)options.IdleTimeout.TotalMinutes,
                null,
                lockReason);
        }

        var activity = session.Activity.Snapshot();
        var remaining = options.IdleTimeout
            - timeProvider.GetElapsedTime(activity.MonotonicTimestamp);
        return new VaultSessionStatus(
            currentState,
            session.SessionId,
            session.StartedAtUtc,
            activity.LastActivityAtUtc,
            activity.LastActivityAtUtc + options.IdleTimeout,
            (int)options.IdleTimeout.TotalMinutes,
            Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds)),
            lockReason);
    }

    private static async Task CopyProtectedFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            VaultFilePermissions.ProtectFile(destinationPath);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to copy the Vault key envelope for backup.",
                exception);
        }
    }

    private static VaultException CreateBusinessStateException(
        VaultSessionState currentState) =>
        currentState switch
        {
            VaultSessionState.Uninitialized => new VaultException(
                VaultErrorCode.VaultNotInitialized,
                "The Vault has not been initialized."),
            VaultSessionState.Initializing
                or VaultSessionState.Unlocking => new VaultException(
                    VaultErrorCode.VaultBusy,
                    "A Vault transition is already in progress."),
            VaultSessionState.LockFailed => new VaultException(
                VaultErrorCode.VaultLockFailed,
                "The Vault requires a process restart."),
            _ => new VaultException(
                VaultErrorCode.VaultLocked,
                "The Vault is locked.")
        };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
    }
}
