using System.IO;
using System.Security.Principal;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;
using Microsoft.Extensions.Hosting;

namespace CipherVault.Host.Service;

public sealed class BrokerPipeHostedService(
    CipherVaultServiceConfigFile configuration,
    CipherVaultServicePaths paths,
    StrictServiceHostOptions options,
    BrokerConnectionRegistry registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableBrokerPipe)
        {
            return;
        }
        var install = CipherVaultServiceConfiguration.LoadInstallManifest(
            paths.PublicManifestPath);
        var ownerSid = configuration.OwnerSid
            ?? throw Invalid("Service owner SID is required.");
        var serviceSid = install.ServiceSid
            ?? throw Invalid("Service SID is required.");
        var servicePolicy = AdminPipeHostedService.CreateServicePolicy(
            configuration,
            serviceSid,
            paths.InstallRoot);
        var verification = WindowsProcessIdentityVerifier.Verify(
            (uint)Environment.ProcessId,
            servicePolicy);
        if (verification.Status != ProcessVerificationStatus.Verified ||
            verification.Evidence is null)
        {
            verification.Evidence?.Dispose();
            throw new VaultException(
                VaultErrorCode.StorageError,
                $"Host service identity verification failed: {verification.ReasonCode}");
        }

        using var evidence = verification.Evidence;
        var brokerPolicy = AdminPipeHostedService.CreateBrokerPolicy(
            configuration,
            ownerSid,
            paths.InstallRoot);
        var pipeSecurity = PipeSecurityFactory.Create(
            [new SecurityIdentifier(serviceSid)],
            [new SecurityIdentifier(ownerSid)]);
        var serverOptions = new BrokerPipeServerOptions(
            configuration.Pipes?.Broker
                ?? throw Invalid("Broker pipe name is required."),
            pipeSecurity,
            Guid.NewGuid().ToString("D"),
            typeof(BrokerPipeHostedService).Assembly.GetName().Version?.ToString()
                ?? "1.0.0",
            evidence.Identity,
            [brokerPolicy],
            ownerSid,
            serviceSid,
            TimeSpan.FromHours(12));

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var server = new BrokerPipeServer(serverOptions);
            try
            {
                await server.AcceptOnceAsync(
                    registry.Set,
                    registry.Clear,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (AdminPipeRpcException exception) when (
                exception.Code == "broker_already_connected")
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(250),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static VaultException Invalid(string message)
    {
        return new VaultException(VaultErrorCode.InvalidInput, message);
    }
}

public sealed class BrokerCredentialPrompt(BrokerConnectionRegistry registry)
    : IHostCredentialPrompt
{
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(2);

    public async Task<HostCredentialPromptResult> RequestAsync(
        HostCredentialPreference preference,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var broker = registry.GetRequired();
        var requestId = Guid.NewGuid();
        var nonce = PipeNonce.Create();
        var expiresAt = DateTimeOffset.UtcNow + RequestLifetime;
        var request = new BrokerCredentialPromptRequest(
            requestId,
            nonce,
            ToPreference(preference),
            message ?? "CipherVault 请求用户授权。",
            expiresAt);
        try
        {
            var result = await broker.CallAsync<
                    BrokerCredentialPromptRequest,
                    BrokerCredentialPromptResult>(
                    "broker.credential.request",
                    request,
                    expiresAt,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.RequestId != requestId ||
                !PipeHandshakeTranscript.FixedTimeEquals(nonce, result.NonceB64Url))
            {
                throw new AdminPipeRpcException(
                    "broker_response_mismatch",
                    "Broker credential response did not match the request.");
            }
            var credentialType = ParseCredentialType(result.CredentialType);
            if (!result.Confirmed)
            {
                return new HostCredentialPromptResult(
                    false,
                    null,
                    credentialType);
            }
            if (string.IsNullOrEmpty(result.Credential))
            {
                throw new AdminPipeRpcException(
                    "invalid_credential",
                    "Broker returned an empty credential.");
            }
            return new HostCredentialPromptResult(
                true,
                result.Credential,
                credentialType);
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
                "The interactive Auth Broker connection is unavailable.");
        }
    }

    private static string ToPreference(HostCredentialPreference preference)
    {
        return preference switch
        {
            HostCredentialPreference.Password => "password",
            HostCredentialPreference.RecoveryKey => "recoveryKey",
            _ => "any"
        };
    }

    private static VaultCredentialType ParseCredentialType(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "password" => VaultCredentialType.Password,
            "recoverykey" or "recovery-key" => VaultCredentialType.RecoveryKey,
            _ => throw new AdminPipeRpcException(
                "broker_response_invalid",
                "Broker returned an unsupported credential type.")
        };
    }
}
