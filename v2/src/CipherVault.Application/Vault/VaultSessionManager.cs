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
                return CreateStatus();
            }
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
        }
        finally
        {
            operationGate.Release();
        }

        return await LockCoreAsync(
            VaultLockReason.UserRequested,
            cancellationToken);
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

        Task<VaultSessionStatus> task;
        VaultSession current;
        lock (transitionSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch ((VaultSessionState)state)
            {
                case VaultSessionState.Locked:
                    return CreateStatus();
                case VaultSessionState.Locking:
                    task = activeLockTask
                        ?? throw new VaultException(
                            VaultErrorCode.VaultBusy,
                            "The Vault lock task is unavailable.");
                    current = null!;
                    break;
                case VaultSessionState.Unlocked:
                    current = session
                        ?? throw new VaultException(
                            VaultErrorCode.VaultBusy,
                            "The unlocked Vault has no active session.");
                    var completion = new TaskCompletionSource<VaultSessionStatus>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    activeLockTask = completion.Task;
                    lockReason = reason;
                    state = (int)VaultSessionState.Locking;
                    task = completion.Task;
                    _ = Task.Run(
                        () => RunLockCleanupAsync(current, completion),
                        CancellationToken.None);
                    break;
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

        return await task.WaitAsync(cancellationToken);
    }

    private async Task RunLockCleanupAsync(
        VaultSession lockedSession,
        TaskCompletionSource<VaultSessionStatus> completion)
    {
        var gateAcquired = false;
        try
        {
            var barrier = LockCleanupBarrierForTest;
            if (barrier is not null)
            {
                await barrier();
            }

            lockedSession.OperationCancellation.Cancel();
            gateAcquired = await TryAcquireOperationGateAsync(timingOptions.CancelGrace);
            if (!gateAcquired)
            {
                lockedSession.Database.TryInterruptActiveCommand();
                gateAcquired = await TryAcquireOperationGateAsync(
                    timingOptions.InterruptGrace);
            }

            if (!gateAcquired)
            {
                SetLockFailed();
                completion.TrySetResult(CreateStatus());
                return;
            }

            await lockedSession.ReleaseAsync();
            lock (transitionSync)
            {
                if (ReferenceEquals(session, lockedSession))
                {
                    session = null;
                }

                state = (int)VaultSessionState.Locked;
                completion.TrySetResult(CreateStatus());
            }
        }
        catch
        {
            SetLockFailed();
            completion.TrySetResult(CreateStatus());
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

    private void SetLockFailed()
    {
        lock (transitionSync)
        {
            state = (int)VaultSessionState.LockFailed;
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

        if (!operationGate.Wait(TimeSpan.FromMilliseconds(250)))
        {
            return false;
        }

        try
        {
            current.OperationCancellation.Cancel();
            await current.ReleaseAsync();
            lock (transitionSync)
            {
                if (ReferenceEquals(session, current))
                {
                    session = null;
                }
            }

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
