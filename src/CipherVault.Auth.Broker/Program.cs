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
        CipherVaultInstallPublicManifest manifest;
        try
        {
            manifest = CipherVaultServiceConfiguration.LoadInstallManifest(
                manifestPath);
        }
        catch (Exception exception)
        {
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
                    PromptForCredentialAsync,
                    PromptForApprovalAsync,
                    cancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (
                cancellationTokenSource.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception exception)
            {
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
        CancellationToken cancellationToken)
    {
        var result = await WindowsOperationApprovalPrompt.RequestAsync(
            request.DisplayTitle,
            request.DisplayDetails,
            cancellationToken);
        return new BrokerApprovalDecision(
            request.ApprovalId,
            request.NonceB64Url,
            result.Approved,
            result.Approved ? null : "user-denied");
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
