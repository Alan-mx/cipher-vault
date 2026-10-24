using System.IO;
using System.Security.Principal;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Operations;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CipherVault.Host.Service;

public sealed class BrokerPipeHostedService(
    CipherVaultServiceConfigFile configuration,
    CipherVaultServicePaths paths,
    StrictServiceHostOptions options,
    BrokerConnectionRegistry registry,
    BrokerActivityProvider activityProvider,
    OwnerSessionRegistry ownerSessions,
    IBrokerLifecycleLog lifecycleLog,
    ILogger<BrokerPipeAcceptLoop> logger) : BackgroundService
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
            TimeSpan.FromHours(12),
            RequireInteractiveSession: true,
            ActivityProvider: activityProvider,
            OwnerSessions: ownerSessions);

        await new BrokerPipeAcceptLoop(
                serverOptions,
                registry,
                activityProvider,
                lifecycleLog,
                logger)
            .RunAsync(stoppingToken)
            .ConfigureAwait(false);
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

public sealed class BrokerApprovalPrompt(
    BrokerConnectionRegistry registry,
    ApprovalStore approvals) : IApprovalPrompt
{
    public async Task<HostApprovalDecision> RequestAsync(
        HostApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var broker = registry.GetRequired();
        try
        {
            var result = await broker.CallAsync<
                    BrokerApprovalRequest,
                    BrokerApprovalDecision>(
                    "broker.approval.request",
                    new BrokerApprovalRequest(
                        request.ApprovalId,
                        request.NonceB64Url,
                        request.Kind.ToString(),
                        request.DisplayTitle,
                        request.DisplayDetails,
                        request.ParametersHash,
                        request.ExpiresAt),
                    request.ExpiresAt,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ApprovalId != request.ApprovalId ||
                !PipeHandshakeTranscript.FixedTimeEquals(
                    result.NonceB64Url,
                    request.NonceB64Url))
            {
                throw new AdminPipeRpcException(
                    "approval_response_mismatch",
                    "Broker approval response did not match the request.");
            }
            return approvals.Decide(
                request,
                result.Approved,
                broker.ProcessSessionId,
                result.DenialReason);
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
                "The interactive Auth Broker is unavailable for approval.");
        }
    }
}

public sealed class BrokerAccessGrantPrompt(BrokerConnectionRegistry registry)
    : IHostAccessGrantPrompt
{
    public async Task<HostAccessGrantPromptResult> RequestAsync(
        HostAccessGrantPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        var broker = registry.GetRequired();
        var approvalId = Guid.NewGuid();
        var nonce = PipeNonce.Create();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var requested = string.Join(
            ",",
            AccessGrantScopes.ToNames(request.RequestedScopes)
                .OrderBy(value => value, StringComparer.Ordinal));
        var allowed = AccessGrantScopes.ToNames(request.AllowedScopes)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        BrokerApprovalDecision result;
        try
        {
            result = await broker.CallAsync<
                    BrokerApprovalRequest,
                    BrokerApprovalDecision>(
                    "broker.approval.request",
                    new BrokerApprovalRequest(
                        approvalId,
                        nonce,
                        "AccessGrant",
                        "授权 Agent 访问 CipherVault",
                        $"客户端 {request.ClientId} 请求权限：{requested}",
                        CanonicalJson.ComputeSha256(
                            JsonSerializer.SerializeToElement(new
                            {
                                request.ClientId,
                                request.ClientInstanceId,
                                request.RequestedScopes
                            })),
                        expiresAt,
                        AccessGrantScopes.ToNames(request.RequestedScopes)
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToArray(),
                        allowed,
                        request.ClientId,
                        request.ClientInstanceId,
                        request.TokenAccessMode.ToString(),
                        request.IdleLifetime.ToString(),
                        request.HardLifetime.ToString()),
                    expiresAt,
                    cancellationToken)
                .ConfigureAwait(false);
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
                "The interactive Auth Broker connection is unavailable for Access Grant.");
        }
        if (result.ApprovalId != approvalId ||
            !PipeHandshakeTranscript.FixedTimeEquals(nonce, result.NonceB64Url))
        {
            throw new AdminPipeRpcException(
                "approval_response_mismatch",
                "Broker Access Grant response did not match.");
        }
        if (!result.Approved)
        {
            return new HostAccessGrantPromptResult(
                Approved: false,
                AccessGrantScope.None);
        }
        return new HostAccessGrantPromptResult(
            Approved: true,
            ResolveApprovedScopes(
                request.RequestedScopes,
                request.AllowedScopes,
                result.ApprovedScopes));
    }

    public static AccessGrantScope ResolveApprovedScopes(
        AccessGrantScope requested,
        AccessGrantScope allowed,
        IReadOnlyList<string>? approvedScopes)
    {
        if (approvedScopes is null)
        {
            throw InvalidApproval("Broker returned no approved scopes.");
        }
        var approved = AccessGrantScope.None;
        foreach (var raw in approvedScopes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw InvalidApproval("Broker returned an empty scope.");
            }
            approved |= raw.Trim().ToLowerInvariant() switch
            {
                "read" => AccessGrantScope.Read,
                "write" => AccessGrantScope.Write,
                "delete" => AccessGrantScope.Delete,
                _ => throw InvalidApproval("Broker returned an unknown scope.")
            };
        }
        var ceiling = requested & allowed;
        if ((approved & ~ceiling) != 0)
        {
            throw InvalidApproval(
                "Broker approved scopes outside the requested set.");
        }
        if ((ceiling & AccessGrantScope.Read) != 0 &&
            (approved & AccessGrantScope.Read) == 0)
        {
            throw InvalidApproval(
                "Broker approval removed the required read scope.");
        }
        if ((approved & AccessGrantScope.Delete) != 0 &&
            (approved & AccessGrantScope.Write) == 0)
        {
            throw InvalidApproval(
                "Broker approval granted delete without write.");
        }
        return AccessGrantScopes.Normalize(approved);
    }

    private static AdminPipeRpcException InvalidApproval(string message)
    {
        return new AdminPipeRpcException(
            "approval_response_invalid",
            message);
    }
}
