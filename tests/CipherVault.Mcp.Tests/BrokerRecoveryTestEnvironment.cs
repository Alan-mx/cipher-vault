using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;
using CipherVault.Host.Mcp;
using CipherVault.Host.Operations;
using CipherVault.Host.Security;
using CipherVault.Host.Service;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;
using Microsoft.Extensions.Logging.Abstractions;

namespace CipherVault.Mcp.Tests;

internal sealed class BrokerRecoveryTestEnvironment : IAsyncDisposable
{
    private static readonly IReadOnlySet<string> ReadScopes =
        new HashSet<string>(StringComparer.Ordinal) { "read" };
    private readonly CancellationTokenSource _timeout;
    private readonly BrokerConnectionRegistry _registry = new();
    private readonly string _runtimeDirectory;
    private readonly string _clientOptionsPath;
    private readonly AccessGrantService _grants;
    private readonly BridgePipeServer _bridgeServer;
    private readonly BridgePipeClientConnection _bridgeConnection;
    private readonly PipeSecurity _pipeSecurity;
    private readonly VerifiedProcessIdentity _serviceIdentity;
    private readonly ProcessVerificationPolicy _hostPolicy;
    private readonly ProcessVerificationPolicy _bridgePolicy;
    private readonly string _ownerSid;
    private readonly Task _brokerLoopTask;
    private readonly Task _bridgeAcceptTask;
    private readonly BrokerGrantHandler _handler;
    private readonly SemaphoreSlim _replacementBrokerGate = new(1, 1);
    private int _brokerStarts;
    private Process? _initialBroker;
    private Process? _replacementBroker;
    private bool _disposed;

    private BrokerRecoveryTestEnvironment(
        BrokerConnectionRegistry registry,
        string runtimeDirectory,
        string clientOptionsPath,
        CancellationTokenSource timeout,
        AccessGrantService grants,
        BridgePipeServer bridgeServer,
        BridgePipeClientConnection bridgeConnection,
        PipeSecurity pipeSecurity,
        VerifiedProcessIdentity serviceIdentity,
        ProcessVerificationPolicy hostPolicy,
        ProcessVerificationPolicy bridgePolicy,
        string ownerSid,
        Task brokerLoopTask,
        Task bridgeAcceptTask,
        BrokerGrantHandler handler)
    {
        _registry = registry;
        _runtimeDirectory = runtimeDirectory;
        _clientOptionsPath = clientOptionsPath;
        _timeout = timeout;
        _grants = grants;
        _bridgeServer = bridgeServer;
        _bridgeConnection = bridgeConnection;
        _pipeSecurity = pipeSecurity;
        _serviceIdentity = serviceIdentity;
        _hostPolicy = hostPolicy;
        _bridgePolicy = bridgePolicy;
        _ownerSid = ownerSid;
        _brokerLoopTask = brokerLoopTask;
        _bridgeAcceptTask = bridgeAcceptTask;
        _handler = handler;
    }

    public CancellationToken Token => _timeout.Token;
    public BrokerConnectionRegistry Registry => _registry;
    public AccessGrantService Grants => _grants;
    public BrokerGrantHandler Handler => _handler;
    public BridgePipeClientConnection BridgeConnection => _bridgeConnection;
    public StrictMcpBridgeClient Mcp { get; private set; } = null!;
    public Process? InitialBroker => _initialBroker;
    public Process? ReplacementBroker => _replacementBroker;
    public int BrokerStarts => Volatile.Read(ref _brokerStarts);

    public static async Task<BrokerRecoveryTestEnvironment> CreateAsync()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "Unable to resolve the current Windows user.");
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var hostImagePath = Environment.ProcessPath!;
        var brokerImagePath = FindTestBrokerExecutable();
        var hostPolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            hostImagePath);
        var brokerPolicy = Policy(
            VerifiedProcessKind.AuthBroker,
            currentUser.Value,
            currentSession,
            brokerImagePath);
        var bridgePolicy = Policy(
            VerifiedProcessKind.McpBridge,
            currentUser.Value,
            currentSession,
            hostImagePath);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            hostPolicy);
        var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(45));
        var security = PipeSecurityFactory.Create(
            [currentUser],
            includeAdministrators: false,
            includeSystem: false);
        var runtimeDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-broker-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeDirectory);
        var lifecycleLogDirectory = Path.Combine(
            runtimeDirectory,
            "logs");
        var brokerPipeName = "CipherVault.Mcp.BrokerRecoveryTest." +
            Guid.NewGuid().ToString("N");
        var bridgePipeName = "CipherVault.Mcp.BridgeRecoveryTest." +
            Guid.NewGuid().ToString("N");
        var registry = new BrokerConnectionRegistry();
        var activityProvider = new BrokerActivityProvider();
        var lifecycleLog = new JsonlBrokerLifecycleLog(
            lifecycleLogDirectory);
        var brokerAcceptLoop = new BrokerPipeAcceptLoop(
            new BrokerPipeServerOptions(
                brokerPipeName,
                security,
                Guid.NewGuid().ToString("D"),
                "1.0.0-test",
                serviceIdentity,
                [brokerPolicy],
                currentUser.Value,
                currentUser.Value,
                TimeSpan.FromMinutes(1),
                RequireInteractiveSession: false,
                ActivityProvider: activityProvider,
                HeartbeatTimeout: TimeSpan.FromSeconds(2)),
            registry,
            activityProvider,
            lifecycleLog,
            NullLogger<BrokerPipeAcceptLoop>.Instance);
        var brokerLoopTask = brokerAcceptLoop.RunAsync(
            timeout.Token);
        var clientOptionsPath = Path.Combine(
            runtimeDirectory,
            "broker-client-options.json");
        var brokerClientOptions = new BrokerPipeClientOptions(
            brokerPipeName,
            brokerPolicy,
            hostPolicy,
            ActivityReportInterval: TimeSpan.FromMilliseconds(500));
        await File.WriteAllTextAsync(
            clientOptionsPath,
            JsonSerializer.Serialize(
                brokerClientOptions,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            timeout.Token);

        var grants = new AccessGrantService();
        var brokerPrompt = new BrokerAccessGrantPrompt(
            registry,
            lifecycleLog);
        var handler = new BrokerGrantHandler(grants, brokerPrompt);
        var bridgeOptions = new BridgePipeServerOptions
        {
            PipeName = bridgePipeName,
            Security = security,
            ServiceInstanceId = Guid.NewGuid().ToString("D"),
            ServiceVersion = "1.0.0-test",
            ServiceIdentity = serviceIdentity,
            AllowedClientPolicy = bridgePolicy,
            OwnerSid = currentUser.Value,
            ServiceSid = currentUser.Value,
            ClientId = "mcp",
            AccessMode = McpAccessMode.ReadOnly,
            BridgeSessionLifetime = TimeSpan.FromMinutes(1),
            Handler = handler
        };
        var bridgeServer = new BridgePipeServer(bridgeOptions);
        var bridgeAcceptTask = bridgeServer.AcceptOnceAsync(
            timeout.Token);
        var bridgeConnection = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                bridgePipeName,
                "mcp",
                McpAccessMode.ReadOnly,
                ReadScopes,
                bridgePolicy,
                hostPolicy),
            timeout.Token);
        var settings = new StrictBridgeSettings(
            new CipherVaultInstallPublicManifest(),
            string.Empty,
            bridgePipeName,
            "mcp",
            McpAccessMode.ReadOnly,
            ReadScopes);
        var environment = new BrokerRecoveryTestEnvironment(
            registry,
            runtimeDirectory,
            clientOptionsPath,
            timeout,
            grants,
            bridgeServer,
            bridgeConnection,
            security,
            serviceIdentity,
            hostPolicy,
            bridgePolicy,
            currentUser.Value,
            brokerLoopTask,
            bridgeAcceptTask,
            handler);
        environment.Mcp = new StrictMcpBridgeClient(
            settings,
            bridgeConnection,
            _ => Task.FromResult<IBridgePipeClientConnection>(
                bridgeConnection),
            environment.EnsureReplacementBrokerStartedAsync);
        return environment;
    }

    public async Task<BrokerRecoveryMcpClient>
        CreateAdditionalMcpClientAsync()
    {
        var pipeName = "CipherVault.Mcp.BridgeRecoveryTest." +
            Guid.NewGuid().ToString("N");
        var options = new BridgePipeServerOptions
        {
            PipeName = pipeName,
            Security = _pipeSecurity,
            ServiceInstanceId = Guid.NewGuid().ToString("D"),
            ServiceVersion = "1.0.0-test",
            ServiceIdentity = _serviceIdentity,
            AllowedClientPolicy = _bridgePolicy,
            OwnerSid = _ownerSid,
            ServiceSid = _ownerSid,
            ClientId = "mcp",
            AccessMode = McpAccessMode.ReadOnly,
            BridgeSessionLifetime = TimeSpan.FromMinutes(1),
            Handler = _handler
        };
        var server = new BridgePipeServer(options);
        var acceptTask = server.AcceptOnceAsync(Token);
        var connection = await BridgePipeClientConnection.ConnectAsync(
            new BridgePipeClientOptions(
                pipeName,
                "mcp",
                McpAccessMode.ReadOnly,
                ReadScopes,
                _bridgePolicy,
                _hostPolicy),
            Token);
        var settings = new StrictBridgeSettings(
            new CipherVaultInstallPublicManifest(),
            string.Empty,
            pipeName,
            "mcp",
            McpAccessMode.ReadOnly,
            ReadScopes);
        var mcp = new StrictMcpBridgeClient(
            settings,
            connection,
            _ => Task.FromResult<IBridgePipeClientConnection>(connection),
            EnsureReplacementBrokerStartedAsync);
        return new BrokerRecoveryMcpClient(
            mcp,
            server,
            acceptTask);
    }

    private async Task<bool> EnsureReplacementBrokerStartedAsync(
        CancellationToken cancellationToken)
    {
        await _replacementBrokerGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (_replacementBroker is null || _replacementBroker.HasExited)
            {
                Interlocked.Increment(ref _brokerStarts);
                _replacementBroker = StartTestBroker(_clientOptionsPath);
            }
            return true;
        }
        catch (Exception exception) when (
            exception is VaultException or
            Win32Exception or
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            NotSupportedException)
        {
            return false;
        }
        finally
        {
            _replacementBrokerGate.Release();
        }
    }

    public Process StartInitialBroker(
        TimeSpan? approvalDelay = null,
        string? approvalStartedPath = null)
    {
        if (_initialBroker is not null)
        {
            throw new InvalidOperationException(
                "The initial Broker has already been started.");
        }
        _initialBroker = StartTestBroker(
            _clientOptionsPath,
            approvalDelay,
            approvalStartedPath);
        return _initialBroker;
    }

    public string CreateTemporaryFilePath(string fileName)
    {
        return Path.Combine(_runtimeDirectory, fileName);
    }

    public Task WaitForRegisteredAsync(Process process)
    {
        return WaitUntilAsync(
            () => RegisteredProcessId(_registry) == (uint?)process.Id,
            TimeSpan.FromSeconds(10));
    }

    public Task WaitForUnavailableAsync()
    {
        return WaitUntilAsync(
            () => !_registry.IsAvailable,
            TimeSpan.FromSeconds(10));
    }

    public async Task<BrokerLifecycleLogEvent[]> ReadLifecycleEventsAsync()
    {
        var lifecycleLogDirectory = Path.Combine(
            _runtimeDirectory,
            "logs");
        await WaitUntilAsync(
            () => Directory.Exists(lifecycleLogDirectory) &&
                Directory.GetFiles(
                    lifecycleLogDirectory,
                    "broker-*.jsonl").Length > 0,
            TimeSpan.FromSeconds(5));
        var lifecycleLogPath = Assert.Single(
            Directory.GetFiles(
                lifecycleLogDirectory,
                "broker-*.jsonl"));
        using var stream = new FileStream(
            lifecycleLogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return lines
            .Select(TryDeserializeLifecycleEvent)
            .OfType<BrokerLifecycleLogEvent>()
            .ToArray();
    }

    public async Task<BrokerLifecycleLogEvent[]> WaitForLifecycleEventsAsync(
        Func<BrokerLifecycleLogEvent[], bool> condition,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ??
            TimeSpan.FromSeconds(10));
        while (true)
        {
            var events = await ReadLifecycleEventsAsync();
            if (condition(events))
            {
                return events;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Expected Broker lifecycle event was not observed.");
            }
            await Task.Delay(50);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            await Mcp.RevokeGrantAsync();
        }
        catch (Exception exception) when (
            exception is IOException or
            ObjectDisposedException or
            OperationCanceledException or
            TimeoutException or
            AdminPipeRpcException or
            PipeRpcException)
        {
        }
        foreach (var process in new[] { _initialBroker, _replacementBroker })
        {
            if (process is null)
            {
                continue;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
            }
            process.Dispose();
        }
        _timeout.Cancel();
        await IgnoreTerminationAsync(_bridgeAcceptTask);
        await IgnoreTerminationAsync(_brokerLoopTask);
        await _bridgeServer.DisposeAsync();
        _grants.Dispose();
        _timeout.Dispose();
        try
        {
            Directory.Delete(_runtimeDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static async Task IgnoreTerminationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or
            TimeoutException or
            IOException or
            ObjectDisposedException)
        {
        }
    }

    private static ProcessVerificationPolicy Policy(
        VerifiedProcessKind kind,
        string userSid,
        uint sessionId,
        string imagePath)
    {
        return new ProcessVerificationPolicy(
            kind,
            userSid,
            ExpectedLogonSid: null,
            sessionId,
            [
                new ProcessImageIdentity(
                    kind,
                    imagePath,
                    WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath),
                    SignerThumbprint: null,
                    SignerSubject: null)
            ],
            AllowedSignerThumbprints: [],
            RequireSha256: true,
            RequireSignature: false);
    }

    private static VerifiedProcessIdentity VerifyIdentity(
        uint processId,
        ProcessVerificationPolicy policy)
    {
        var result = WindowsProcessIdentityVerifier.Verify(processId, policy);
        Assert.Equal(ProcessVerificationStatus.Verified, result.Status);
        using var evidence = Assert.IsType<VerifiedProcessEvidence>(
            result.Evidence);
        return evidence.Identity;
    }

    private static uint? RegisteredProcessId(BrokerConnectionRegistry registry)
    {
        try
        {
            return registry.GetRequired().ClientIdentity.ProcessId;
        }
        catch (AdminPipeRpcException exception) when (
            exception.Code == "approval_broker_unavailable")
        {
            return null;
        }
    }

    private static string FindTestBrokerExecutable()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name
            ?? throw new InvalidOperationException(
                "Unable to resolve test build configuration.");
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var executable = Path.Combine(
            repositoryRoot,
            "tests",
            "CipherVault.TestBroker",
            "bin",
            configuration,
            "net10.0-windows",
            "CipherVault.TestBroker.exe");
        Assert.True(
            File.Exists(executable),
            $"Test Broker executable was not found: {executable}");
        return executable;
    }

    private static Process StartTestBroker(
        string optionsPath,
        TimeSpan? approvalDelay = null,
        string? approvalStartedPath = null)
    {
        var startInfo = new ProcessStartInfo(FindTestBrokerExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(optionsPath);
        if (approvalDelay is { } delay && delay > TimeSpan.Zero)
        {
            startInfo.Environment[
                "CIPHER_VAULT_TEST_APPROVAL_DELAY_MS"] =
                ((int)delay.TotalMilliseconds).ToString();
        }
        if (!string.IsNullOrWhiteSpace(approvalStartedPath))
        {
            startInfo.Environment[
                "CIPHER_VAULT_TEST_APPROVAL_STARTED_FILE"] =
                approvalStartedPath;
        }
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Unable to start the test Broker process.");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Broker recovery test condition was not reached.");
            }
            await Task.Delay(25);
        }
    }

    private static BrokerLifecycleLogEvent? TryDeserializeLifecycleEvent(
        string line)
    {
        try
        {
            return JsonSerializer.Deserialize<BrokerLifecycleLogEvent>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class BrokerRecoveryMcpClient(
    StrictMcpBridgeClient mcp,
    BridgePipeServer server,
    Task acceptTask) : IAsyncDisposable
{
    public StrictMcpBridgeClient Mcp { get; } = mcp;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Mcp.RevokeGrantAsync();
        }
        catch (Exception exception) when (
            exception is IOException or
            ObjectDisposedException or
            OperationCanceledException or
            TimeoutException or
            AdminPipeRpcException or
            PipeRpcException)
        {
        }
        await server.DisposeAsync();
        try
        {
            await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or
            TimeoutException or
            IOException or
            ObjectDisposedException)
        {
        }
    }
}

internal sealed class BrokerGrantHandler(
    AccessGrantService grants,
    BrokerAccessGrantPrompt prompt) : IBridgeSessionHandler
{
    private const string SuccessfulRead =
        """{"ok":true,"document":{"documentId":"d80c2e83-636f-47a8-a3f2-e473beec9922"}}""";
    private readonly Guid _vaultSessionId = Guid.NewGuid();
    private readonly DateTimeOffset _vaultExpiresAt =
        DateTimeOffset.UtcNow.AddMinutes(30);
    private int _toolCallCount;
    private int _toolExecutionCount;

    public Guid? LastGrantId { get; private set; }
    public int ToolCallCount => Volatile.Read(ref _toolCallCount);
    public int ToolExecutionCount =>
        Volatile.Read(ref _toolExecutionCount);

    public async Task<BridgeIssuedGrant> RequestGrantAsync(
        BridgeProcessSession session,
        BridgeGrantRequest request,
        CancellationToken cancellationToken)
    {
        if (!AccessGrantPathPolicy.TryParse(
                request.Scopes,
                out var requestedScopes,
                out var requestedPaths))
        {
            throw new AdminPipeRpcException(
                "invalid_input",
                "Bridge scope is invalid.");
        }
        var outcome = await grants.RequestAsync(
            session.ClientId,
            session.BridgeInstanceId,
            session.AccessMode,
            requestedScopes,
            requestedPaths,
            new AccessGrantVaultGeneration(
                _vaultSessionId,
                _vaultExpiresAt),
            async (approval, token) =>
            {
                var result = await prompt.RequestAsync(
                    new HostAccessGrantPromptRequest(
                        approval.ClientId,
                        approval.ClientInstanceId,
                        approval.TokenAccessMode,
                        approval.RequestedScopes,
                        approval.AllowedScopes,
                        approval.PathPolicy,
                        grants.IdleLifetime,
                        grants.HardLifetime),
                    token);
                return new AccessGrantApprovalResult(
                    result.Approved,
                    result.ApprovedScopes);
            },
            session.ProcessSessionId,
            cancellationToken);
        var issued = outcome.IssuedGrant
            ?? throw new AdminPipeRpcException(
                outcome.ReasonCode ?? "access_denied",
                "Bridge Access Grant was denied.");
        var record = issued.Record;
        LastGrantId = record.GrantId;
        return new BridgeIssuedGrant(
            new BridgeGrantData(
                record.GrantId,
                record.ClientId,
                session.AccessMode == McpAccessMode.ReadWrite
                    ? "readWrite"
                    : "readOnly",
                (record.PathPolicy ?? AccessGrantPathPolicy.Unrestricted)
                    .ToScopeNames(record.Scopes)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                record.HardExpiresAt!.Value),
            new AccessGrantContext(
                record.GrantId,
                record.ClientId,
                record.ClientInstanceId,
                record.Scopes,
                record.VaultSessionId!.Value,
                record.VerifiedProcessSessionId,
                record.PathPolicy));
    }

    public Task<string> CallToolAsync(
        BridgeProcessSession session,
        AccessGrantContext? grant,
        BridgeToolCallRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _toolCallCount);
        if (!string.Equals(
                request.ToolName,
                "read_note",
                StringComparison.Ordinal))
        {
            return Task.FromResult(BridgeToolEnvelope.Error(
                "unsupported_tool",
                "The recovery test only supports read_note."));
        }
        if (grant is null)
        {
            return Task.FromResult(BridgeToolEnvelope.Error(
                "access_grant_required",
                "Request a Bridge Access Grant first."));
        }
        var used = grants.UseBridgeGrant(grant);
        if (used.Status != AccessGrantValidationStatus.Valid ||
            used.Grant is null)
        {
            return Task.FromResult(BridgeToolEnvelope.Error(
                used.Status == AccessGrantValidationStatus.Expired
                    ? "access_grant_expired"
                    : "access_grant_revoked",
                "Access Grant is no longer valid."));
        }
        if (!AccessGrantScopes.Includes(
                used.Grant.Scopes,
                AccessGrantScope.Read))
        {
            return Task.FromResult(BridgeToolEnvelope.Error(
                "access_denied",
                "Access Grant does not allow read_note."));
        }
        Interlocked.Increment(ref _toolExecutionCount);
        return Task.FromResult(SuccessfulRead);
    }

    public Task<BridgeStatusData> GetStatusAsync(
        BridgeProcessSession session,
        AccessGrantContext? grant,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> scopes = grant is null
            ? Array.Empty<string>()
            : AccessGrantScopes.ToNames(grant.Scopes).ToArray();
        return Task.FromResult(new BridgeStatusData(
            grant is not null,
            session.ClientId,
            session.AccessMode == McpAccessMode.ReadWrite
                ? "readWrite"
                : "readOnly",
            session.ProcessSessionId,
            session.BridgeInstanceId,
            session.ExpiresAt,
            scopes,
            GrantExpiresAt: null));
    }

    public Task CloseSessionAsync(
        Guid processSessionId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        return grants.RevokeByProcessSessionAsync(
            processSessionId,
            reasonCode);
    }

}
