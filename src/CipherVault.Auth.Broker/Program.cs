using System.Diagnostics;
using System.Security.Principal;
using CipherVault.Auth.Win;
using CipherVault.Core.Configuration;
using CipherVault.Core.Session;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;

namespace CipherVault.Auth.Broker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var manifestPath = GetOption(args, "--manifest") ??
            CipherVaultServicePaths.Default.PublicManifestPath;
        var log = new BrokerLog();
        CipherVaultInstallPublicManifest manifest;
        try
        {
            manifest = CipherVaultServiceConfiguration.LoadInstallManifest(
                manifestPath);
        }
        catch (Exception exception)
        {
            log.Write("startup", "error", message: exception.Message);
            Trace.TraceError(exception.Message);
            return 1;
        }
        var ownerSid = WindowsIdentity.GetCurrent().User;
        if (ownerSid is null ||
            !string.Equals(
                ownerSid.Value,
                manifest.OwnerSid,
                StringComparison.OrdinalIgnoreCase))
        {
            log.Write("startup", "owner_mismatch");
            return 1;
        }
        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            $@"Local\CipherVault.AuthBroker.{manifest.InstallId:N}",
            out var createdNew);
        if (!createdNew)
        {
            return 0;
        }
        var pipeName = manifest.Pipes?.Broker;
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return 1;
        }

        using var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArguments) =>
        {
            eventArguments.Cancel = true;
            cancellationTokenSource.Cancel();
        };
        BrokerPipeClientOptions options;
        try
        {
            options = StrictServiceClientPolicyFactory.CreateBrokerOptions(
                manifest,
                pipeName,
                ownerSid.Value,
                (uint)Process.GetCurrentProcess().SessionId);
        }
        catch (Exception exception)
        {
            log.Write("startup", "error", message: exception.Message);
            Trace.TraceError(exception.Message);
            return 1;
        }
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                await using var connection =
                    await BrokerPipeClientConnection.ConnectAsync(
                        options,
                        cancellationTokenSource.Token);
                await connection.RunAsync(
                    (request, token) =>
                        PromptForCredentialAsync(request, log, token),
                    (request, token) =>
                        PromptForApprovalAsync(request, log, token),
                    (request, token) =>
                        PromptForTransferAsync(request, log, token),
                    cancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (
                cancellationTokenSource.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception exception)
            {
                log.Write("connection", "failed", message: exception.Message);
                Trace.TraceWarning(exception.Message);
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
            }
        }
        return 0;
    }

    private static async Task<BrokerCredentialPromptResult> PromptForCredentialAsync(
        BrokerCredentialPromptRequest request,
        BrokerLog log,
        CancellationToken cancellationToken)
    {
        var preferredType = request.Preference.ToLowerInvariant() switch
        {
            "password" => VaultCredentialType.Password,
            "recoverykey" or "recovery-key" => VaultCredentialType.RecoveryKey,
            _ => (VaultCredentialType?)null
        };
        var result = await WindowsVaultCredentialPrompt.RequestAsync(
            "CipherVault 授权",
            request.Message,
            preferredType,
            cancellationToken);
        log.Write(
            "credential",
            result.Confirmed ? "approved" : "denied",
            request.RequestId,
            "credential",
            result.Confirmed ? null : "user-denied");
        return new BrokerCredentialPromptResult(
            request.RequestId,
            request.NonceB64Url,
            result.Confirmed,
            result.Confirmed ? result.Credential : null,
            result.CredentialType == VaultCredentialType.RecoveryKey
                ? "recoveryKey"
                : "password",
            result.Confirmed ? null : "user-denied");
    }

    private static async Task<BrokerApprovalDecision> PromptForApprovalAsync(
        BrokerApprovalRequest request,
        BrokerLog log,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                request.OperationKind,
                "AccessGrant",
                StringComparison.Ordinal) &&
            request.RequestedScopes is not null &&
            request.AllowedScopes is not null)
        {
            var scopeResult = await WindowsAccessGrantPrompt.RequestAsync(
                request.DisplayTitle,
                new AccessGrantPromptRequest(
                    request.ClientId ?? "unknown-agent",
                    request.ClientInstanceId ?? Guid.Empty,
                    request.TokenAccessMode ?? string.Empty,
                    request.AllowedScopes.ToHashSet(StringComparer.Ordinal),
                    request.RequestedScopes.ToHashSet(StringComparer.Ordinal),
                    request.IdleLifetime ?? "默认",
                    request.HardLifetime ?? "默认"),
                cancellationToken);
            var approvedScopes = scopeResult.Confirmed
                ? scopeResult.ToScopeNames()
                : null;
            log.Write(
                "approval",
                scopeResult.Confirmed ? "approved" : "denied",
                request.ApprovalId,
                request.OperationKind,
                scopeResult.Confirmed ? null : "user-denied");
            return new BrokerApprovalDecision(
                request.ApprovalId,
                request.NonceB64Url,
                scopeResult.Confirmed,
                scopeResult.Confirmed ? null : "user-denied",
                approvedScopes);
        }

        var result = await WindowsOperationApprovalPrompt.RequestAsync(
            request.DisplayTitle,
            request.DisplayDetails,
            cancellationToken);
        log.Write(
            "approval",
            result.Approved ? "approved" : "denied",
            request.ApprovalId,
            request.OperationKind,
            result.Approved ? null : "user-denied");
        return new BrokerApprovalDecision(
            request.ApprovalId,
            request.NonceB64Url,
            result.Approved,
            result.Approved ? null : "user-denied");
    }

    private static async Task<BrokerTransferResult> PromptForTransferAsync(
        BrokerTransferRequest request,
        BrokerLog log,
        CancellationToken cancellationToken)
    {
        var result = await WindowsFileTransferPrompt.RequestAsync(
            new FileTransferPromptRequest(
                request.TransferId,
                request.Direction,
                request.PayloadKind,
                request.StagingPath,
                request.SuggestedPath,
                request.ExpiresAt),
            cancellationToken);
        log.Write(
            "transfer",
            result.Completed ? "completed" : "failed",
            request.TransferId,
            request.Direction,
            result.ErrorCode);
        return new BrokerTransferResult(
            result.TransferId,
            result.Completed,
            result.FinalPath,
            result.ErrorCode);
    }

    private static string? GetOption(string[] args, string option)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }
}
