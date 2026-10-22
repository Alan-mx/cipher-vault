using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
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
    private readonly Action<string> _cleanup;
    private bool _disposed;

    public TransferWorkspace(
        Guid transferId,
        string rootPath,
        string payloadPath,
        Action<string> cleanup)
    {
        TransferId = transferId;
        RootPath = rootPath;
        PayloadPath = payloadPath;
        _cleanup = cleanup;
    }

    public Guid TransferId { get; }
    public string RootPath { get; }
    public string PayloadPath { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cleanup(RootPath);
    }
}

public sealed class TransferCoordinator(CipherVaultServicePaths paths)
{
    public TransferWorkspace CreateWorkspace(
        HostTransferDirection direction,
        HostTransferPayloadKind payloadKind,
        string ownerSidValue,
        string? logonSidValue,
        string payloadName)
    {
        var transferId = Guid.NewGuid();
        var rootPath = Path.Combine(
            paths.TransferRoot,
            transferId.ToString("N"));
        var payloadPath = Path.Combine(rootPath, payloadName);
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
            if (payloadKind == HostTransferPayloadKind.Directory)
            {
                Directory.CreateDirectory(payloadPath);
                VaultFileSecurity.ProtectDirectoryWithRules(payloadPath, rules);
            }
            return new TransferWorkspace(
                transferId,
                rootPath,
                payloadPath,
                Cleanup);
        }
        catch
        {
            TryDeleteDirectory(rootPath);
            throw;
        }
    }

    public void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(paths.TransferRoot)
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

    private static void Cleanup(string rootPath)
    {
        TryDeleteDirectory(rootPath);
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
