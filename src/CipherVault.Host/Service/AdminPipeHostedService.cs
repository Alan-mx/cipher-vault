using System.IO;
using System.Security.Principal;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Http;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CipherVault.Host.Service;

public sealed record StrictServiceHostOptions(
    bool EnableAdminPipe = true,
    bool EnableBrokerPipe = true,
    bool EnableBridgePipe = true);

public sealed class StrictAdminStatusProvider
{
    private readonly Func<CancellationToken, Task<HostSessionSnapshot>> _getSnapshot;
    private readonly Func<bool> _brokerAvailable;

    public StrictAdminStatusProvider(
        Func<CancellationToken, Task<HostSessionSnapshot>> getSnapshot,
        bool brokerAvailable = false)
        : this(getSnapshot, () => brokerAvailable)
    {
    }

    public StrictAdminStatusProvider(
        Func<CancellationToken, Task<HostSessionSnapshot>> getSnapshot,
        Func<bool> brokerAvailable)
    {
        _getSnapshot = getSnapshot;
        _brokerAvailable = brokerAvailable;
    }

    public async Task<AdminPipeStatusData> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await _getSnapshot(cancellationToken).ConfigureAwait(false);
        return new AdminPipeStatusData(
            typeof(StrictAdminStatusProvider).Assembly
                .GetName().Version?.ToString() ?? "1.0.0",
            PipeProtocol.Version,
            CipherVaultServiceConfiguration.StrictModeValue,
            _brokerAvailable(),
            HostDtoMapper.ToSessionStatus(snapshot));
    }
}

public sealed class StrictAdminLockProvider(
    Func<string, CancellationToken, Task<HostSessionSnapshot>> lockSession)
{
    public async Task<HostSessionStatusData> LockAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var snapshot = await lockSession(reason, cancellationToken)
            .ConfigureAwait(false);
        return HostDtoMapper.ToSessionStatus(snapshot);
    }
}

public sealed class StrictAdminUnlockProvider(
    Func<
        HostCredentialPreference,
        CancellationToken,
        Task<HostSessionSnapshot>> unlockSession)
{
    public async Task<HostSessionStatusData> UnlockAsync(
        HostCredentialPreference preference,
        CancellationToken cancellationToken)
    {
        var snapshot = await unlockSession(preference, cancellationToken)
            .ConfigureAwait(false);
        return HostDtoMapper.ToSessionStatus(snapshot);
    }
}

public sealed class StrictAdminInitializeProvider(
    Func<string, CancellationToken, Task<HostInitializeData>> initializeVault)
{
    public Task<HostInitializeData> InitializeAsync(
        string vaultPath,
        CancellationToken cancellationToken)
    {
        return initializeVault(vaultPath, cancellationToken);
    }
}

public sealed class AdminPipeHostedService(
    CipherVaultServiceConfigFile configuration,
    CipherVaultServicePaths paths,
    StrictServiceHostOptions options,
    StrictAdminStatusProvider statusProvider,
    StrictAdminLockProvider lockProvider,
    StrictAdminUnlockProvider unlockProvider,
    StrictAdminInitializeProvider initializeProvider,
    StrictAdminVaultRpcProvider vaultRpcProvider,
    AdminSessionStore adminSessions,
    HostSessionCoordinator sessions,
    IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableAdminPipe)
        {
            return;
        }
        var install = CipherVaultServiceConfiguration.LoadInstallManifest(
            paths.PublicManifestPath);
        var ownerSid = configuration.OwnerSid
            ?? throw Invalid("Service owner SID is required.");
        var serviceSid = install.ServiceSid
            ?? throw Invalid("Service SID is required.");
        var servicePolicy = CreateServicePolicy(
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
        var serviceIdentity = evidence.Identity;
        var clientPolicies = CreateClientPolicies(
            configuration,
            ownerSid,
            paths.InstallRoot);
        var ownerIdentifier = new SecurityIdentifier(ownerSid);
        var serviceIdentifier = new SecurityIdentifier(serviceSid);
        var pipeSecurity = PipeSecurityFactory.Create(
            [serviceIdentifier],
            [ownerIdentifier]);
        var sessionOptions = VaultSessionOptions.FromMinutes(
            configuration.Session?.IdleMinutes ?? 15,
            configuration.Session?.HardHours ?? 4);
        var serverOptions = new AdminPipeServerOptions(
            configuration.Pipes?.Admin
                ?? throw Invalid("Admin pipe name is required."),
            pipeSecurity,
            Guid.NewGuid().ToString("D"),
            CipherVaultServiceConfiguration.StrictModeValue,
            typeof(AdminPipeHostedService).Assembly.GetName().Version?.ToString()
                ?? "1.0.0",
            serviceIdentity,
            clientPolicies,
            ownerSid,
            serviceSid,
            sessionOptions.IdleTimeout,
            sessionOptions.HardLifetime,
            (_, cancellationToken) =>
                statusProvider.GetStatusAsync(cancellationToken),
            (_, reason, cancellationToken) =>
                lockProvider.LockAsync(reason, cancellationToken),
            (_, preference, cancellationToken) =>
                unlockProvider.UnlockAsync(preference, cancellationToken),
            (_, vaultPath, cancellationToken) =>
                initializeProvider.InitializeAsync(
                    vaultPath,
                    cancellationToken),
            (session, method, payload, cancellationToken) =>
                vaultRpcProvider.HandleAsync(
                    session,
                    method,
                    payload,
                    cancellationToken),
            adminSessions,
            StopHostCallback: () =>
            {
                applicationLifetime.StopApplication();
                return Task.CompletedTask;
            });

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var server = new AdminPipeServer(serverOptions);
            try
            {
                using var clientConnection = sessions.EnterClient();
                _ = await server.AcceptOnceAsync(stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public static ProcessVerificationPolicy CreateServicePolicy(
        CipherVaultServiceConfigFile configuration,
        string serviceSid,
        string installRoot)
    {
        return CreatePolicy(
            configuration,
            VerifiedProcessKind.HostService,
            serviceSid,
            installRoot,
            expectedSessionId: 0,
            required: true);
    }

    public static IReadOnlyList<ProcessVerificationPolicy> CreateClientPolicies(
        CipherVaultServiceConfigFile configuration,
        string ownerSid,
        string installRoot)
    {
        var result = new List<ProcessVerificationPolicy>();
        foreach (var kind in new[]
                 {
                     VerifiedProcessKind.AdminUi,
                     VerifiedProcessKind.AdminCli
                 })
        {
            var policy = CreatePolicy(
                configuration,
                kind,
                ownerSid,
                installRoot,
                expectedSessionId: null,
                required: false);
            if (policy.AllowedImages.Count > 0)
            {
                result.Add(policy);
            }
        }
        if (result.Count == 0)
        {
            throw Invalid("At least one Admin UI or CLI image is required.");
        }
        return result;
    }

    public static ProcessVerificationPolicy CreateBrokerPolicy(
        CipherVaultServiceConfigFile configuration,
        string ownerSid,
        string installRoot)
    {
        return CreatePolicy(
            configuration,
            VerifiedProcessKind.AuthBroker,
            ownerSid,
            installRoot,
            expectedSessionId: null,
            required: true);
    }

    public static ProcessVerificationPolicy CreateBridgePolicy(
        CipherVaultServiceConfigFile configuration,
        string ownerSid,
        string installRoot)
    {
        return CreatePolicy(
            configuration,
            VerifiedProcessKind.McpBridge,
            ownerSid,
            installRoot,
            expectedSessionId: null,
            required: true);
    }

    private static ProcessVerificationPolicy CreatePolicy(
        CipherVaultServiceConfigFile configuration,
        VerifiedProcessKind kind,
        string expectedUserSid,
        string installRoot,
        uint? expectedSessionId,
        bool required)
    {
        var policy = configuration.ProcessPolicy
            ?? throw Invalid("Process policy is required.");
        var images = (policy.AllowedImages ?? [])
            .Where(image =>
                Enum.TryParse<VerifiedProcessKind>(
                    image.Kind,
                    ignoreCase: true,
                    out var imageKind) &&
                imageKind == kind)
            .Select(image =>
            {
                var path = Path.GetFullPath(image.Path!);
                if (!IsUnderPath(path, installRoot) || !File.Exists(path))
                {
                    throw Invalid(
                        $"Process image is not installed under {installRoot}: {path}");
                }
                return new ProcessImageIdentity(
                    kind,
                    path,
                    image.Sha256!,
                    image.SignerThumbprint,
                    image.SignerSubject);
            })
            .ToArray();
        if (required && images.Length == 0)
        {
            throw Invalid($"Process image policy is missing for {kind}.");
        }
        if (images.Length == 0)
        {
            return new ProcessVerificationPolicy(
                kind,
                expectedUserSid,
                ExpectedLogonSid: null,
                ExpectedSessionId: expectedSessionId,
                AllowedImages: [],
                AllowedSignerThumbprints:
                    policy.AllowedSignerThumbprints ?? [],
                RequireSha256: true,
                RequireSignature: true);
        }
        return new ProcessVerificationPolicy(
            kind,
            expectedUserSid,
            ExpectedLogonSid: null,
            ExpectedSessionId: expectedSessionId,
            AllowedImages: images,
            AllowedSignerThumbprints:
                policy.AllowedSignerThumbprints ?? [],
            RequireSha256: policy.RequireSha256 ?? true,
            RequireSignature: true);
    }

    private static bool IsUnderPath(string path, string parent)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar);
        return normalizedPath.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static VaultException Invalid(string message)
    {
        return new VaultException(VaultErrorCode.InvalidInput, message);
    }
}
