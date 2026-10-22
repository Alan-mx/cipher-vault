using System.Collections.Concurrent;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Host.Contracts;
using CipherVault.Host.Session;
using CipherVault.Ipc;

namespace CipherVault.Host.Security;

public enum HostTransferDirection
{
    Import,
    Export
}

public enum HostTransferPayloadKind
{
    File,
    Directory
}

public sealed record HostTransferRequest(
    Guid TransferId,
    HostTransferDirection Direction,
    HostTransferPayloadKind PayloadKind,
    string StagingPath,
    string SuggestedPath,
    DateTimeOffset ExpiresAt);

public sealed record HostTransferResult(
    Guid TransferId,
    bool Completed,
    string? FinalPath,
    string? ErrorCode);

public interface ITransferPrompt
{
    Task<HostTransferResult> TransferAsync(
        HostTransferRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class TransferWorkspace : IDisposable
{
    private readonly Action<TransferWorkspace> _cleanup;
    private bool _disposed;

    public TransferWorkspace(
        Guid transferId,
        string rootPath,
        string payloadPath,
        HostTransferDirection direction,
        HostTransferPayloadKind payloadKind,
        string ownerSid,
        string? logonSid,
        DateTimeOffset expiresAt,
        Action<TransferWorkspace> cleanup)
    {
        TransferId = transferId;
        RootPath = rootPath;
        PayloadPath = payloadPath;
        Direction = direction;
        PayloadKind = payloadKind;
        OwnerSid = ownerSid;
        LogonSid = logonSid;
        ExpiresAt = expiresAt;
        _cleanup = cleanup;
    }

    public Guid TransferId { get; }
    public string RootPath { get; }
    public string PayloadPath { get; }
    public HostTransferDirection Direction { get; }
    public HostTransferPayloadKind PayloadKind { get; }
    public string OwnerSid { get; }
    public string? LogonSid { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string ManifestPath => Path.Combine(RootPath, "transfer.json");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cleanup(this);
    }
}

public sealed class TransferCoordinator : IDisposable
{
    public static readonly TimeSpan DefaultWorkspaceLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, TransferWorkspace> _activeWorkspaces = new();
    private readonly CipherVaultServicePaths _paths;
    private readonly TimeSpan _workspaceLifetime;
    private readonly Timer _timer;
    private bool _disposed;

    public TransferCoordinator(CipherVaultServicePaths paths)
        : this(paths, null)
    {
    }

    public TransferCoordinator(
        CipherVaultServicePaths paths,
        TimeSpan? workspaceLifetime)
    {
        _paths = paths;
        _workspaceLifetime = workspaceLifetime ?? DefaultWorkspaceLifetime;
        _timer = new Timer(
            _ =>
            {
                try
                {
                    SweepExpired();
                    CleanupOrphanedTransfers();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            null,
            DefaultSweepInterval,
            DefaultSweepInterval);
        CleanupOrphanedTransfers();
    }

    public TransferWorkspace CreateWorkspace(
        HostTransferDirection direction,
        HostTransferPayloadKind payloadKind,
        string ownerSidValue,
        string? logonSidValue,
        string payloadName)
    {
        ThrowIfDisposed();
        var transferId = Guid.NewGuid();
        var rootPath = Path.Combine(
            _paths.TransferRoot,
            transferId.ToString("N"));
        var payloadPath = Path.Combine(rootPath, payloadName);
        var expiresAt = DateTimeOffset.UtcNow + _workspaceLifetime;
        TransferWorkspace workspace;
        try
        {
            Directory.CreateDirectory(rootPath);
            var service = WindowsIdentity.GetCurrent().User
                ?? throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Unable to resolve the service SID.");
            var owner = new SecurityIdentifier(ownerSidValue);
            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            var system = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            var rules = new List<VaultFileAccessRule>
            {
                new(service, FileSystemRights.FullControl, InheritToChildren: true),
                new(administrators, FileSystemRights.FullControl, InheritToChildren: true),
                new(system, FileSystemRights.FullControl, InheritToChildren: true),
                new(
                    owner,
                    direction == HostTransferDirection.Import
                        ? FileSystemRights.Modify
                        : FileSystemRights.ReadAndExecute,
                    InheritToChildren: true)
            };
            if (!string.IsNullOrWhiteSpace(logonSidValue) &&
                !string.Equals(
                    logonSidValue,
                    ownerSidValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                rules.Add(new VaultFileAccessRule(
                    new SecurityIdentifier(logonSidValue),
                    direction == HostTransferDirection.Import
                        ? FileSystemRights.Modify
                        : FileSystemRights.ReadAndExecute,
                    InheritToChildren: true));
            }
            VaultFileSecurity.ProtectDirectoryWithRules(rootPath, rules);
            if (payloadKind == HostTransferPayloadKind.Directory &&
                direction == HostTransferDirection.Import)
            {
                Directory.CreateDirectory(payloadPath);
                VaultFileSecurity.ProtectDirectoryWithRules(payloadPath, rules);
            }
            workspace = new TransferWorkspace(
                transferId,
                rootPath,
                payloadPath,
                direction,
                payloadKind,
                ownerSidValue,
                logonSidValue,
                expiresAt,
                Cleanup);
        }
        catch
        {
            TryDeleteDirectory(rootPath);
            throw;
        }
        _activeWorkspaces[transferId] = workspace;
        return workspace;
    }

    public TransferManifest FreezeManifest(
        TransferWorkspace workspace)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(workspace);
        if (!Directory.Exists(workspace.RootPath))
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Transfer workspace is no longer available.");
        }

        var manifest = WriteManifest(workspace);
        ApplyFrozenRules(workspace);
        return manifest;
    }

    public int SweepExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var pair in _activeWorkspaces.ToArray())
        {
            if (pair.Value.ExpiresAt > now)
            {
                continue;
            }
            if (!_activeWorkspaces.TryRemove(pair.Key, out var workspace))
            {
                continue;
            }
            count++;
            workspace.Dispose();
        }
        return count;
    }

    public int CleanupOrphanedTransfers()
    {
        if (!Directory.Exists(_paths.TransferRoot))
        {
            return 0;
        }

        var count = 0;
        foreach (var directory in Directory.EnumerateDirectories(
                     _paths.TransferRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (Guid.TryParseExact(
                    Path.GetFileName(directory),
                    "N",
                    out var transferId) &&
                _activeWorkspaces.ContainsKey(transferId))
            {
                continue;
            }
            TryDeleteDirectory(directory);
            count++;
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
        foreach (var pair in _activeWorkspaces.ToArray())
        {
            if (_activeWorkspaces.TryRemove(pair.Key, out var workspace))
            {
                workspace.Dispose();
            }
        }
    }

    public void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(_paths.TransferRoot)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Transfer path is outside the staging root.");
        }
        var current = new DirectoryInfo(
            Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath)!);
        while (current.FullName.StartsWith(
                   root,
                   StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Transfer path contains a reparse point.");
            }
            if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = current.Parent
                ?? throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Transfer path has no staging parent.");
        }
    }

    private static TransferManifest WriteManifest(TransferWorkspace workspace)
    {
        var files = Directory.EnumerateFiles(
                workspace.RootPath,
                "*",
                SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFullPath(path),
                workspace.ManifestPath,
                StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var info = new FileInfo(path);
                using var stream = info.OpenRead();
                return new TransferFileEntry(
                    Path.GetRelativePath(
                        workspace.RootPath,
                        path).Replace('\\', '/'),
                    info.Length,
                    Convert.ToHexString(SHA256.HashData(stream)));
            })
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var manifest = new TransferManifest(
            "cipher-vault-transfer",
            1,
            workspace.TransferId,
            workspace.Direction,
            workspace.PayloadKind,
            files,
            DateTimeOffset.UtcNow,
            workspace.ExpiresAt);
        File.WriteAllText(
            workspace.ManifestPath,
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions
                {
                    Converters =
                    {
                        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                    }
                }));
        return manifest;
    }

    private void ApplyFrozenRules(TransferWorkspace workspace)
    {
        var service = WindowsIdentity.GetCurrent().User
            ?? throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to resolve the service SID.");
        var owner = new SecurityIdentifier(workspace.OwnerSid);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            null);
        var rules = new List<VaultFileAccessRule>
        {
            new(service, FileSystemRights.FullControl, InheritToChildren: true),
            new(administrators, FileSystemRights.FullControl, InheritToChildren: true),
            new(system, FileSystemRights.FullControl, InheritToChildren: true),
            new(owner, FileSystemRights.ReadAndExecute, InheritToChildren: true)
        };
        if (!string.IsNullOrWhiteSpace(workspace.LogonSid) &&
            !string.Equals(
                workspace.LogonSid,
                workspace.OwnerSid,
                StringComparison.OrdinalIgnoreCase))
        {
            rules.Add(new VaultFileAccessRule(
                new SecurityIdentifier(workspace.LogonSid),
                FileSystemRights.ReadAndExecute,
                InheritToChildren: true));
        }
        VaultFileSecurity.ProtectDirectoryWithRules(workspace.RootPath, rules);
    }

    private static void Cleanup(TransferWorkspace workspace)
    {
        TryDeleteDirectory(workspace.RootPath);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class BrokerTransferPrompt(BrokerConnectionRegistry registry)
    : ITransferPrompt
{
    public async Task<HostTransferResult> TransferAsync(
        HostTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        var broker = registry.GetRequired();
        try
        {
            var result = await broker.CallAsync<
                    BrokerTransferRequest,
                    BrokerTransferResult>(
                    "broker.transfer",
                    new BrokerTransferRequest(
                        request.TransferId,
                        request.Direction.ToString().ToLowerInvariant(),
                        request.PayloadKind.ToString().ToLowerInvariant(),
                        request.StagingPath,
                        request.SuggestedPath,
                        request.ExpiresAt),
                    request.ExpiresAt,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.TransferId != request.TransferId)
            {
                throw new AdminPipeRpcException(
                    "transfer_invalid",
                    "Broker transfer response did not match the request.");
            }
            return new HostTransferResult(
                result.TransferId,
                result.Completed,
                result.FinalPath,
                result.ErrorCode);
        }
        catch (AdminPipeRpcException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is TimeoutException or
            IOException or
            InvalidDataException or
            ObjectDisposedException)
        {
            throw new AdminPipeRpcException(
                "approval_broker_unavailable",
                "The interactive Auth Broker is unavailable for file transfer.");
        }
    }
}
