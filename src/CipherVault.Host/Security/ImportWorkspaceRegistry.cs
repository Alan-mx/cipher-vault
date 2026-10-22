using System.Collections.Concurrent;

namespace CipherVault.Host.Security;

public sealed class ImportWorkspaceRegistry : IDisposable
{
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, TransferWorkspace> _workspaces = new();
    private readonly TimeProvider _timeProvider;
    private readonly Timer _timer;
    private bool _disposed;

    public ImportWorkspaceRegistry(
        TimeProvider? timeProvider = null,
        TimeSpan? sweepInterval = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        var interval = sweepInterval ?? DefaultSweepInterval;
        _timer = new Timer(
            _ =>
            {
                try
                {
                    SweepExpired();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            state: null,
            dueTime: interval,
            period: interval);
    }

    public int Count => _workspaces.Count;

    public bool Contains(Guid jobId)
    {
        if (!_workspaces.TryGetValue(jobId, out var workspace))
        {
            return false;
        }
        if (workspace.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return true;
        }
        if (_workspaces.TryRemove(jobId, out var expired))
        {
            expired.Dispose();
        }
        return false;
    }

    public bool TryAdd(Guid jobId, TransferWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _workspaces.TryAdd(jobId, workspace);
    }

    public bool TryTake(Guid jobId, out TransferWorkspace? workspace)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_workspaces.TryRemove(jobId, out workspace))
        {
            workspace = null;
            return false;
        }
        if (workspace.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            workspace.Dispose();
            workspace = null;
            return false;
        }
        return true;
    }

    public int SweepExpired()
    {
        if (_disposed)
        {
            return 0;
        }
        var now = _timeProvider.GetUtcNow();
        var count = 0;
        foreach (var pair in _workspaces.ToArray())
        {
            if (pair.Value.ExpiresAt > now)
            {
                continue;
            }
            if (_workspaces.TryRemove(pair.Key, out var workspace))
            {
                workspace.Dispose();
                count++;
            }
        }
        return count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Dispose();
        foreach (var pair in _workspaces.ToArray())
        {
            if (_workspaces.TryRemove(pair.Key, out var workspace))
            {
                workspace.Dispose();
            }
        }
    }
}
