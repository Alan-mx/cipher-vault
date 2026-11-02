using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CipherVault.Application.Backup;
using CipherVault.Application.Vault;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Server.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CipherVault.Server.Backup;

internal sealed class GitBackupCoordinator :
    BackgroundService,
    IDocumentMutationObserver
{
    private readonly GitBackupOptions options;
    private readonly GitBackupStateStore stateStore;
    private readonly IGitBackupRepository repository;
    private readonly BackupService backupService;
    private readonly BackupStorageOptions backupStorage;
    private readonly ApplicationPaths paths;
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<GitBackupCoordinator> logger;
    private readonly Channel<GitBackupWakeup> wakeups =
        Channel.CreateUnbounded<GitBackupWakeup>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    private readonly SemaphoreSlim runGate = new(1, 1);
    private readonly SemaphoreSlim stateWriteGate = new(1, 1);
    private readonly object sync = new();
    private readonly Lazy<Task> initialization;
    private GitBackupPersistedState? state;
    private GitBackupRuntimeStatus status;
    private GitRepositoryInspection? inspection;
    private long mutationGeneration;
    private CancellationToken serviceCancellation;

    public GitBackupCoordinator(
        GitBackupOptions options,
        GitBackupStateStore stateStore,
        IGitBackupRepository repository,
        BackupService backupService,
        BackupStorageOptions backupStorage,
        ApplicationPaths paths,
        VaultSessionManager sessionManager,
        TimeProvider timeProvider,
        ILogger<GitBackupCoordinator> logger)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        this.backupStorage = backupStorage ?? throw new ArgumentNullException(nameof(backupStorage));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        status = CreateInitialStatus();
        initialization = new Lazy<Task>(
            InitializeAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async ValueTask NotifyAsync(
        DocumentMutationKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return;
        }

        await EnsureInitializedAsync(CancellationToken.None);
        Interlocked.Increment(ref mutationGeneration);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        lock (sync)
        {
            if (state is null)
            {
                return;
            }

            state = state with
            {
                Dirty = true,
                FirstDirtyAtUtc = state.Dirty
                    ? state.FirstDirtyAtUtc
                    : now,
                LastMutationAtUtc = now
            };
            status = status with
            {
                State = GetActiveState(),
                Pending = true
            };
        }

        _ = await SaveStateSafelyAsync(CancellationToken.None);
        wakeups.Writer.TryWrite(new GitBackupWakeup(
            GitBackupWakeupReason.DocumentMutation,
            now));
    }

    public async Task<GitBackupRuntimeStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        lock (sync)
        {
            return status;
        }
    }

    internal async Task RunOnceForTestAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await ProcessPendingAsync(
            bypassDelay: true,
            cancellationToken);
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        serviceCancellation = stoppingToken;
        sessionManager.StateChanged += OnVaultStateChanged;
        try
        {
            await EnsureInitializedAsync(stoppingToken);
            if (!options.IsUsable)
            {
                return;
            }

            await foreach (var wakeup in wakeups.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessPendingAsync(
                    bypassDelay: false,
                    stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        finally
        {
            sessionManager.StateChanged -= OnVaultStateChanged;
        }
    }

    private async Task InitializeAsync()
    {
        if (!options.Enabled)
        {
            lock (sync)
            {
                status = new GitBackupRuntimeStatus(
                    GitBackupState.Disabled,
                    Pending: false,
                    PushPending: false,
                    NextAttemptAtUtc: null,
                    LastSuccessAtUtc: null,
                    LastCommitShortSha: null,
                    LastPushAtUtc: null,
                    LastErrorCode: null,
                    RotationRequired: false,
                    RepositoryObjectBytes: null);
            }
            return;
        }

        if (!options.IsUsable)
        {
            lock (sync)
            {
                status = CreateInitialStatus() with
                {
                    State = GitBackupState.Failed,
                    LastErrorCode = options.ConfigurationErrorCode
                        ?? GitBackupErrorCodes.ConfigurationInvalid
                };
            }
            return;
        }

        var fingerprint = CreateRepositoryFingerprint(options);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        GitBackupStateLoadResult loaded;
        try
        {
            loaded = await stateStore.LoadAsync(
                paths.GitBackupStatePath,
                fingerprint,
                now);
        }
        catch
        {
            loaded = new GitBackupStateLoadResult(
                GitBackupPersistedState.CreateInitial(fingerprint, now),
                WasReset: true,
                SafeErrorCode: GitBackupErrorCodes.StateWriteFailed);
        }
        lock (sync)
        {
            state = loaded.State;
        }

        var safeError = loaded.SafeErrorCode;
        try
        {
            inspection = await repository.InspectAsync(
                options,
                CancellationToken.None);
            UpdateStatusFromState(
                GetActiveState(),
                safeError);
        }
        catch (GitCommandException exception)
        {
            safeError = exception.Code;
            UpdateStatusFromState(
                GitBackupState.Failed,
                safeError);
        }
        catch
        {
            safeError = GitBackupErrorCodes.RepositoryInvalid;
            UpdateStatusFromState(
                GitBackupState.Failed,
                safeError);
        }

        if (loaded.WasReset)
        {
            _ = await SaveStateSafelyAsync(CancellationToken.None);
        }

        if (state is { Dirty: true } or { PendingPushCommitSha: not null })
        {
            wakeups.Writer.TryWrite(new GitBackupWakeup(
                GitBackupWakeupReason.ConfigurationChanged,
                now));
        }
    }

    private async Task ProcessPendingAsync(
        bool bypassDelay,
        CancellationToken cancellationToken)
    {
        await runGate.WaitAsync(cancellationToken);
        try
        {
            var current = SnapshotState();
            if (current.PendingPushCommitSha is not null)
            {
                if (!bypassDelay && current.NextAttemptAtUtc is { } retryAt
                    && retryAt > timeProvider.GetUtcNow())
                {
                    return;
                }

                if (!await TryPushPendingAsync(cancellationToken))
                {
                    return;
                }
                current = SnapshotState();
            }

            if (!current.Dirty
                && current.PendingPushCommitSha is null)
            {
                UpdateStatusFromState(GitBackupState.Idle, null);
                return;
            }

            if (current.Dirty
                && !bypassDelay
                && !await WaitForDueAsync(cancellationToken))
            {
                return;
            }

            var vaultStatus = await sessionManager.GetStatusAsync(cancellationToken);
            if (vaultStatus.State != VaultSessionState.Unlocked)
            {
                UpdateStatusFromState(GitBackupState.Pending, null);
                return;
            }

            await RunBackupAndSyncAsync(cancellationToken);
        }
        finally
        {
            runGate.Release();
        }
    }

    private async Task<bool> WaitForDueAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var current = SnapshotState();
            if (!current.Dirty)
            {
                return false;
            }

            var dueAt = CalculateDueAt(current, options);
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            if (now >= dueAt)
            {
                return true;
            }

            using var delayCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(
                dueAt - now,
                timeProvider,
                delayCancellation.Token);
            var wakeTask = wakeups.Reader
                .WaitToReadAsync(cancellationToken)
                .AsTask();
            var completed = await Task.WhenAny(delayTask, wakeTask);
            delayCancellation.Cancel();
            if (completed == delayTask)
            {
                await delayTask;
                return true;
            }

            while (wakeups.Reader.TryRead(out _))
            {
                // Drain coalesced mutation/timer signals and recompute the due time.
            }
        }
    }

    private async Task RunBackupAndSyncAsync(
        CancellationToken cancellationToken)
    {
        var generationAtStart = Interlocked.Read(ref mutationGeneration);
        UpdateStatusFromState(GitBackupState.Running, null);
        try
        {
            inspection = await repository.InspectAsync(
                options,
                cancellationToken);
            if (HasBlockingGitOperation(inspection))
            {
                await ScheduleRetryAsync(
                    GitBackupErrorCodes.RepositoryBusy,
                    cancellationToken);
                return;
            }

            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var rotationRequired = IsRotationRequired(inspection, now);
            if (rotationRequired)
            {
                await CreateLocalBackupIfNeededAsync(cancellationToken);
                var currentGeneration = Interlocked.Read(ref mutationGeneration);
                if (currentGeneration == generationAtStart)
                {
                    await MarkDirtyClearedAsync(
                        GitBackupState.RotationRequired,
                        GitBackupErrorCodes.RotationRequired,
                        cancellationToken);
                }
                else
                {
                    UpdateStatusFromState(
                        GitBackupState.RotationRequired,
                        GitBackupErrorCodes.RotationRequired);
                }

                return;
            }

            var sourcePath = await CreateLocalBackupIfNeededAsync(
                cancellationToken);
            var published = await repository.PublishSnapshotAsync(
                options,
                sourcePath,
                now,
                cancellationToken);
            var generationAtCommit = Interlocked.Read(ref mutationGeneration);
            await MarkCommitAsync(
                published.CommitSha,
                generationAtCommit == generationAtStart,
                cancellationToken);
            var current = SnapshotState();
            if (current.PendingPushCommitSha is null)
            {
                UpdateStatusFromState(
                    current.Dirty
                        ? GitBackupState.Pending
                        : GitBackupState.Idle,
                    null);
                return;
            }

            _ = await TryPushPendingAsync(cancellationToken);
        }
        catch (VaultException exception)
            when (exception.Code is VaultErrorCode.VaultLocked
                or VaultErrorCode.VaultNotInitialized)
        {
            UpdateStatusFromState(GitBackupState.Pending, null);
        }
        catch (VaultException exception)
            when (exception.Code == VaultErrorCode.BackupInProgress)
        {
            await ScheduleRetryAsync(
                GitBackupErrorCodes.BackupBusy,
                cancellationToken);
        }
        catch (GitCommandException exception)
        {
            try
            {
                _ = await CreateLocalBackupIfNeededAsync(cancellationToken);
            }
            catch
            {
                // Preserve the Git failure as the primary sync error.
            }
            await ScheduleRetryAsync(exception.Code, cancellationToken);
        }
        catch (VaultException)
        {
            await ScheduleRetryAsync(
                GitBackupErrorCodes.SnapshotFailed,
                cancellationToken);
        }
        catch
        {
            await ScheduleRetryAsync(
                GitBackupErrorCodes.InternalError,
                cancellationToken);
        }
    }

    private async Task<string> CreateLocalBackupIfNeededAsync(
        CancellationToken cancellationToken)
    {
        var current = SnapshotState();
        if (current.PendingLocalBackupFileName is { } existing
            && TryResolveBackupPath(existing, out var existingPath))
        {
            return existingPath;
        }

        var actor = new VaultActorContext(
            VaultActorKind.System,
            "git_backup_auto");
        var created = await backupService.CreateAsync(actor, cancellationToken);
        var path = ResolveBackupPath(created.Item.FileName);
        lock (sync)
        {
            state = (state ?? throw new InvalidOperationException()) with
            {
                PendingLocalBackupFileName = created.Item.FileName
            };
        }
        _ = await SaveStateSafelyAsync(cancellationToken);
        return path;
    }

    private async Task<bool> TryPushPendingAsync(
        CancellationToken cancellationToken)
    {
        var current = SnapshotState();
        if (current.PendingPushCommitSha is null)
        {
            return true;
        }

        var pushed = await repository.PushAsync(options, cancellationToken);
        if (!pushed.Succeeded)
        {
            await ScheduleRetryAsync(
                pushed.ErrorCode ?? GitBackupErrorCodes.PushFailed,
                cancellationToken);
            return false;
        }

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        lock (sync)
        {
            state = state! with
            {
                PendingPushCommitSha = null,
                NextAttemptAtUtc = null,
                ConsecutiveFailures = 0,
                LastErrorCode = null,
                LastPushAtUtc = now,
                LastSuccessAtUtc = now
            };
        }
        _ = await SaveStateSafelyAsync(cancellationToken);
        UpdateStatusFromState(
            SnapshotState().Dirty
                ? GitBackupState.Pending
                : GitBackupState.Idle,
            null);
        return true;
    }

    private async Task MarkCommitAsync(
        string? commitSha,
        bool generationUnchanged,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        lock (sync)
        {
            var current = state ?? throw new InvalidOperationException();
            state = current with
            {
                Dirty = generationUnchanged ? false : current.Dirty,
                FirstDirtyAtUtc = generationUnchanged
                    ? null
                    : current.FirstDirtyAtUtc,
                LastMutationAtUtc = generationUnchanged
                    ? null
                    : current.LastMutationAtUtc,
                PendingLocalBackupFileName = null,
                PendingPushCommitSha = commitSha,
                LastCommitSha = commitSha ?? current.LastCommitSha,
                LastAttemptAtUtc = now,
                NextAttemptAtUtc = null,
                LastErrorCode = null
            };
        }
        _ = await SaveStateSafelyAsync(cancellationToken);
    }

    private async Task MarkDirtyClearedAsync(
        GitBackupState nextState,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            state = state! with
            {
                Dirty = false,
                FirstDirtyAtUtc = null,
                LastMutationAtUtc = null,
                PendingLocalBackupFileName = null,
                LastAttemptAtUtc = timeProvider.GetUtcNow().ToUniversalTime(),
                LastErrorCode = errorCode
            };
        }
        _ = await SaveStateSafelyAsync(cancellationToken);
        UpdateStatusFromState(nextState, errorCode);
    }

    private async Task ScheduleRetryAsync(
        string errorCode,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var delay = options.RetryDelay;
        lock (sync)
        {
            var current = state ?? throw new InvalidOperationException();
            var failures = Math.Min(current.ConsecutiveFailures + 1, 20);
            var multiplier = 1 << Math.Min(failures - 1, 5);
            delay = TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromHours(1).Ticks,
                options.RetryDelay.Ticks * multiplier));
            state = current with
            {
                LastAttemptAtUtc = now,
                NextAttemptAtUtc = now.Add(delay),
                ConsecutiveFailures = failures,
                LastErrorCode = errorCode
            };
            status = status with
            {
                State = failures >= 20
                    ? GitBackupState.Failed
                    : GitBackupState.RetryWait,
                Pending = current.Dirty || current.PendingPushCommitSha is not null,
                PushPending = current.PendingPushCommitSha is not null,
                NextAttemptAtUtc = now.Add(delay),
                LastErrorCode = errorCode
            };
        }
        _ = await SaveStateSafelyAsync(cancellationToken);
        ScheduleWakeup(delay, cancellationToken);
    }

    private async Task<bool> SaveStateSafelyAsync(
        CancellationToken cancellationToken)
    {
        GitBackupPersistedState snapshot;
        lock (sync)
        {
            if (state is null)
            {
                return false;
            }
            snapshot = state;
        }

        try
        {
            await stateWriteGate.WaitAsync(cancellationToken);
            try
            {
                await stateStore.SaveAsync(
                    paths.GitBackupStatePath,
                    snapshot,
                    cancellationToken);
            }
            finally
            {
                stateWriteGate.Release();
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            lock (sync)
            {
                status = status with
                {
                    State = GitBackupState.Failed,
                    LastErrorCode = GitBackupErrorCodes.StateWriteFailed
                };
            }
            logger.LogWarning(
                new EventId(7101, "GitBackupStateWriteFailed"),
                "Git backup state could not be persisted.");
            return false;
        }
    }

    private void UpdateStatusFromState(
        GitBackupState requestedState,
        string? errorCode)
    {
        lock (sync)
        {
            var current = state;
            status = new GitBackupRuntimeStatus(
                requestedState,
                Pending: current?.Dirty == true
                    || current?.PendingPushCommitSha is not null,
                PushPending: current?.PendingPushCommitSha is not null,
                NextAttemptAtUtc: current?.NextAttemptAtUtc,
                LastSuccessAtUtc: current?.LastSuccessAtUtc,
                LastCommitShortSha: ShortSha(current?.LastCommitSha),
                LastPushAtUtc: current?.LastPushAtUtc,
                LastErrorCode: errorCode ?? current?.LastErrorCode,
                RotationRequired: requestedState == GitBackupState.RotationRequired,
                RepositoryObjectBytes: inspection?.ObjectBytes);
        }
    }

    private GitBackupState GetActiveState()
    {
        lock (sync)
        {
            if (status.State == GitBackupState.RotationRequired)
            {
                return GitBackupState.RotationRequired;
            }
            return state?.Dirty == true
                || state?.PendingPushCommitSha is not null
                ? GitBackupState.Pending
                : GitBackupState.Idle;
        }
    }

    private bool IsRotationRequired(
        GitRepositoryInspection current,
        DateTimeOffset now)
    {
        if (current.ObjectBytes >= options.RotationBytes)
        {
            return true;
        }

        return current.FirstCommitAtUtc is { } first
            && now - first >= options.RotationAge;
    }

    private static bool HasBlockingGitOperation(
        GitRepositoryInspection current) =>
        current.HasMergeOrRebaseState || current.HasConflicts;

    internal static DateTimeOffset CalculateDueAt(
        GitBackupPersistedState current,
        GitBackupOptions options)
    {
        var first = current.FirstDirtyAtUtc
            ?? throw new InvalidOperationException(
                "Dirty backup state has no first dirty timestamp.");
        var last = current.LastMutationAtUtc ?? first;
        var debounceAt = last + options.DebounceDelay;
        var maxAt = first + options.MaxDelay;
        return debounceAt < maxAt ? debounceAt : maxAt;
    }

    private GitBackupPersistedState SnapshotState()
    {
        lock (sync)
        {
            return state ?? throw new InvalidOperationException();
        }
    }

    private string ResolveBackupPath(string fileName)
    {
        if (!TryResolveBackupPath(fileName, out var path))
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Created backup file is unavailable.");
        }
        return path;
    }

    private bool TryResolveBackupPath(
        string fileName,
        out string path)
    {
        path = string.Empty;
        if (!BackupFileName.TryParse(fileName, out _))
        {
            return false;
        }

        var candidate = Path.Combine(backupStorage.BackupDirectory, fileName);
        var canonical = PathSafety.Canonicalize(candidate, "Backup file");
        if (!PathSafety.GetRelation(
                canonical,
                backupStorage.BackupDirectory).IsChildOf
            || !PathSafety.IsRegularFile(canonical))
        {
            return false;
        }

        path = canonical;
        return true;
    }

    private void OnVaultStateChanged(VaultStateChangedEvent change)
    {
        if (change.Status.State == VaultSessionState.Unlocked)
        {
            wakeups.Writer.TryWrite(new GitBackupWakeup(
                GitBackupWakeupReason.VaultUnlocked,
                change.ObservedAtUtc));
        }
    }

    private void ScheduleWakeup(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (serviceCancellation.IsCancellationRequested
            || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(
                        delay,
                        timeProvider,
                        serviceCancellation);
                    wakeups.Writer.TryWrite(new GitBackupWakeup(
                        GitBackupWakeupReason.RetryElapsed,
                        timeProvider.GetUtcNow()));
                }
                catch (OperationCanceledException)
                {
                    // Host shutdown.
                }
            },
            CancellationToken.None);
    }

    private async Task EnsureInitializedAsync(
        CancellationToken cancellationToken)
    {
        await initialization.Value.WaitAsync(cancellationToken);
    }

    private GitBackupRuntimeStatus CreateInitialStatus() =>
        new(
            options.Enabled
                ? options.IsUsable
                    ? GitBackupState.Idle
                    : GitBackupState.Failed
                : GitBackupState.Disabled,
            Pending: false,
            PushPending: false,
            NextAttemptAtUtc: null,
            LastSuccessAtUtc: null,
            LastCommitShortSha: null,
            LastPushAtUtc: null,
            LastErrorCode: options.Enabled && !options.IsUsable
                ? options.ConfigurationErrorCode
                    ?? GitBackupErrorCodes.ConfigurationInvalid
                : null,
            RotationRequired: false,
            RepositoryObjectBytes: null);

    internal static string CreateRepositoryFingerprint(
        GitBackupOptions options)
    {
        var value =
            $"{options.RepositoryPath}\0{options.Remote}\0{options.Branch}";
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static string? ShortSha(string? value) =>
        value is null ? null : value[..12];
}
