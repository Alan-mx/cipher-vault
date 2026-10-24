using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Service;
using CipherVault.Host.Session;
using CipherVault.Ipc;
using CipherVault.Security.Win;
using Microsoft.Extensions.Logging.Abstractions;

namespace CipherVault.Host.Tests;

public sealed class BrokerPipeTests
{
    [Fact]
    public async Task BrokerReturnsCredentialForServiceRequest()
    {
        var pipeName = "CipherVault.Broker.Test." + Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var brokerPolicy = Policy(
            VerifiedProcessKind.AuthBroker,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var activityProvider = new BrokerActivityProvider();
        await using var server = new BrokerPipeServer(
            new BrokerPipeServerOptions(
                pipeName,
                PipeSecurityFactory.Create(
                    [currentUser],
                    includeAdministrators: false,
                    includeSystem: false),
                Guid.NewGuid().ToString("D"),
                "1.0.0-test",
                serviceIdentity,
                [brokerPolicy],
                currentUser.Value,
                currentUser.Value,
                TimeSpan.FromMinutes(5),
                RequireInteractiveSession: false,
                ActivityProvider: activityProvider));
        var connected = new TaskCompletionSource<BrokerPipeConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new BrokerConnectionRegistry();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = server.AcceptOnceAsync(
            connection =>
            {
                registry.Set(connection);
                connected.TrySetResult(connection);
            },
            registry.Clear,
            timeout.Token);

        await using var client = await BrokerPipeClientConnection.ConnectAsync(
            new BrokerPipeClientOptions(
                pipeName,
                brokerPolicy,
                servicePolicy));
        var readTask = client.RunAsync(
            (request, _) =>
            {
                Assert.Equal("password", request.Preference);
                return Task.FromResult(new BrokerCredentialPromptResult(
                    request.RequestId,
                    request.NonceB64Url,
                    Confirmed: true,
                    Credential: "test-password",
                    CredentialType: "password",
                    DenialReason: null));
            },
            (request, _) => Task.FromResult(new BrokerApprovalDecision(
                request.ApprovalId,
                request.NonceB64Url,
                Approved: true,
                DenialReason: null)),
            timeout.Token);
        var connection = await connected.Task.WaitAsync(timeout.Token);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var request = new BrokerCredentialPromptRequest(
            Guid.NewGuid(),
            PipeNonce.Create(),
            "password",
            "Unlock",
            expiresAt);

        var result = await connection.CallAsync<
            BrokerCredentialPromptRequest,
            BrokerCredentialPromptResult>(
            "broker.credential.request",
            request,
            expiresAt,
            timeout.Token);

        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(request.NonceB64Url, result.NonceB64Url);
        Assert.Equal("test-password", result.Credential);
        await WaitUntilAsync(
            () => activityProvider.GetSnapshot().SystemIdleTime <
                TimeSpan.FromDays(1),
            TimeSpan.FromSeconds(5));
        Assert.Equal("credential-prompt.v1", Assert.Single(
            client.Capabilities.Features));
        var approvals = new ApprovalStore();
        using var approvalParameters = JsonDocument.Parse(
            $$"""{"documentId":"{{Guid.NewGuid():D}}"}""");
        var operation = approvals.Prepare(
            ApprovalOperationKind.PurgeNote,
            Guid.NewGuid(),
            Guid.NewGuid(),
            approvalParameters.RootElement);
        var approvalRequest = approvals.CreateRequest(
            operation,
            "永久删除",
            "永久删除测试文档");
        var approvalPrompt = new BrokerApprovalPrompt(
            registry,
            approvals);
        var approvalDecision = await approvalPrompt.RequestAsync(
            approvalRequest,
            timeout.Token);
        Assert.True(approvalDecision.Approved);

        timeout.Cancel();
        await readTask;
        await client.DisposeAsync();
        await acceptTask;
        Assert.False(registry.IsAvailable);
        Assert.Equal(
            TimeSpan.FromDays(1),
            activityProvider.GetSnapshot().SystemIdleTime);
    }

    [Fact]
    public async Task InvalidBrokerConnectionDoesNotRegister()
    {
        var pipeName = "CipherVault.Broker.InvalidTest." + Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var brokerPolicy = Policy(
            VerifiedProcessKind.AuthBroker,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var activityProvider = new BrokerActivityProvider();
        await using var server = new BrokerPipeServer(
            new BrokerPipeServerOptions(
                pipeName,
                PipeSecurityFactory.Create(
                    [currentUser],
                    includeAdministrators: false,
                    includeSystem: false),
                Guid.NewGuid().ToString("D"),
                "1.0.0-test",
                serviceIdentity,
                [brokerPolicy],
                currentUser.Value,
                currentUser.Value,
                TimeSpan.FromMinutes(5),
                RequireInteractiveSession: false,
                ActivityProvider: activityProvider));
        var registry = new BrokerConnectionRegistry();
        var connectedCount = 0;
        var disconnectedCount = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = server.AcceptOnceAsync(
            connection =>
            {
                connectedCount++;
                registry.Set(connection);
            },
            connection =>
            {
                disconnectedCount++;
                registry.Clear(connection);
            },
            timeout.Token);

        await WriteInvalidHandshakeAsync(pipeName);

        await Assert.ThrowsAnyAsync<Exception>(
            () => acceptTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, connectedCount);
        Assert.Equal(0, disconnectedCount);
        Assert.False(registry.IsAvailable);
        Assert.Equal(
            TimeSpan.FromDays(1),
            activityProvider.GetSnapshot().SystemIdleTime);
    }

    [Theory]
    [InlineData(
        AccessGrantScope.Read | AccessGrantScope.Write | AccessGrantScope.Delete,
        AccessGrantScope.Read | AccessGrantScope.Write | AccessGrantScope.Delete,
        new[] { "read", "write" },
        AccessGrantScope.Read | AccessGrantScope.Write)]
    [InlineData(
        AccessGrantScope.Read,
        AccessGrantScope.Read | AccessGrantScope.Write,
        new[] { "read" },
        AccessGrantScope.Read)]
    [InlineData(
        AccessGrantScope.Read | AccessGrantScope.Write | AccessGrantScope.Delete,
        AccessGrantScope.Read | AccessGrantScope.Write | AccessGrantScope.Delete,
        new[] { "read", "write", "delete" },
        AccessGrantScope.Read | AccessGrantScope.Write | AccessGrantScope.Delete)]
    public void ApprovedGrantScopesCanOnlyReduceRequestedScopes(
        AccessGrantScope requested,
        AccessGrantScope allowed,
        string[] approvedScopes,
        AccessGrantScope expected)
    {
        var result = BrokerAccessGrantPrompt.ResolveApprovedScopes(
            requested,
            allowed,
            approvedScopes);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ApprovedGrantScopesRejectEscalationAndInvalidCombinations()
    {
        AssertInvalidGrantScopes(["write"]);
        AssertInvalidGrantScopes(["read", "delete"]);
        AssertInvalidGrantScopes(
            ["read", "write"],
            requested: AccessGrantScope.Read);
        AssertInvalidGrantScopes(null);
    }

    private static void AssertInvalidGrantScopes(
        IReadOnlyList<string>? approvedScopes,
        AccessGrantScope requested =
            AccessGrantScope.Read | AccessGrantScope.Write | AccessGrantScope.Delete)
    {
        var exception = Assert.Throws<AdminPipeRpcException>(
            () => BrokerAccessGrantPrompt.ResolveApprovedScopes(
                requested,
                requested,
                approvedScopes));
        Assert.Equal("approval_response_invalid", exception.Code);
    }

    [Fact]
    public void RegistryReportsBrokerUnavailable()
    {
        var registry = new BrokerConnectionRegistry();

        Assert.False(registry.IsAvailable);
        var exception = Assert.Throws<AdminPipeRpcException>(
            registry.GetRequired);
        Assert.Equal("approval_broker_unavailable", exception.Code);
    }

    [Fact]
    public async Task ExpiredRequestFaultsConnectionAndAllowsRegistryReplacement()
    {
        var pipeName = "CipherVault.Broker.TimeoutTest." +
            Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var brokerPolicy = Policy(
            VerifiedProcessKind.AuthBroker,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var activityProvider = new BrokerActivityProvider();
        var options = new BrokerPipeServerOptions(
            pipeName,
            PipeSecurityFactory.Create(
                [currentUser],
                includeAdministrators: false,
                includeSystem: false),
            Guid.NewGuid().ToString("D"),
            "1.0.0-test",
            serviceIdentity,
            [brokerPolicy],
            currentUser.Value,
            currentUser.Value,
            TimeSpan.FromMinutes(5),
            RequireInteractiveSession: false,
            ActivityProvider: activityProvider);
        var registry = new BrokerConnectionRegistry();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using (var server = new BrokerPipeServer(options))
        {
            var connected = new TaskCompletionSource<BrokerPipeConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptTask = server.AcceptOnceAsync(
                connection =>
                {
                    registry.Set(connection);
                    connected.TrySetResult(connection);
                },
                registry.Clear,
                timeout.Token);
            await using var client = await BrokerPipeClientConnection.ConnectAsync(
                new BrokerPipeClientOptions(
                    pipeName,
                    brokerPolicy,
                    servicePolicy));
            var readTask = client.RunAsync(
                async (request, cancellationToken) =>
                {
                    if (string.Equals(
                            request.Preference,
                            "block",
                            StringComparison.Ordinal))
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            cancellationToken);
                    }
                    return new BrokerCredentialPromptResult(
                        request.RequestId,
                        request.NonceB64Url,
                        Confirmed: true,
                        Credential: "test-password",
                        CredentialType: "password",
                        DenialReason: null);
                },
                timeout.Token);
            var connection = await connected.Task.WaitAsync(timeout.Token);
            var request = new BrokerCredentialPromptRequest(
                Guid.NewGuid(),
                PipeNonce.Create(),
                "block",
                "Block until the Host request expires.",
                DateTimeOffset.UtcNow.AddSeconds(2));

            await Assert.ThrowsAsync<TimeoutException>(() =>
                connection.CallAsync<
                    BrokerCredentialPromptRequest,
                    BrokerCredentialPromptResult>(
                    "broker.credential.request",
                    request,
                    DateTimeOffset.UtcNow.AddMilliseconds(200),
                    timeout.Token));

            Assert.False(registry.IsAvailable);
            await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await readTask;
            }
            catch (Exception exception) when (
                exception is IOException or OperationCanceledException)
            {
            }
        }

        await using (var replacementServer = new BrokerPipeServer(options))
        {
            var replacementConnected =
                new TaskCompletionSource<BrokerPipeConnection>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementAccept = replacementServer.AcceptOnceAsync(
                connection =>
                {
                    registry.Set(connection);
                    replacementConnected.TrySetResult(connection);
                },
                registry.Clear,
                timeout.Token);
            await using var replacementClient =
                await BrokerPipeClientConnection.ConnectAsync(
                    new BrokerPipeClientOptions(
                        pipeName,
                        brokerPolicy,
                        servicePolicy));
            var replacementRead = replacementClient.RunAsync(
                (request, _) => Task.FromResult(
                    new BrokerCredentialPromptResult(
                        request.RequestId,
                        request.NonceB64Url,
                        Confirmed: true,
                        Credential: "replacement",
                        CredentialType: "password",
                        DenialReason: null)),
                timeout.Token);

            _ = await replacementConnected.Task.WaitAsync(timeout.Token);
            Assert.True(registry.IsAvailable);

            timeout.Cancel();
            await replacementRead;
            await replacementAccept;
        }
    }

    [Fact]
    public async Task BrokerConnectionExpiryReleasesRegistration()
    {
        var pipeName = "CipherVault.Broker.ExpiryTest." +
            Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var brokerPolicy = Policy(
            VerifiedProcessKind.AuthBroker,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var options = new BrokerPipeServerOptions(
            pipeName,
            PipeSecurityFactory.Create(
                [currentUser],
                includeAdministrators: false,
                includeSystem: false),
            Guid.NewGuid().ToString("D"),
            "1.0.0-test",
            serviceIdentity,
            [brokerPolicy],
            currentUser.Value,
            currentUser.Value,
            TimeSpan.FromSeconds(2),
            RequireInteractiveSession: false,
            ActivityProvider: new BrokerActivityProvider());
        var registry = new BrokerConnectionRegistry();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using (var server = new BrokerPipeServer(options))
        {
            var connected = new TaskCompletionSource<BrokerPipeConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptTask = server.AcceptOnceAsync(
                connection =>
                {
                    registry.Set(connection);
                    connected.TrySetResult(connection);
                },
                registry.Clear,
                timeout.Token);
            await using var client = await BrokerPipeClientConnection.ConnectAsync(
                new BrokerPipeClientOptions(
                    pipeName,
                    brokerPolicy,
                    servicePolicy));
            var readTask = client.RunAsync(
                (request, _) => Task.FromResult(
                    new BrokerCredentialPromptResult(
                        request.RequestId,
                        request.NonceB64Url,
                        Confirmed: false,
                        Credential: null,
                        CredentialType: "password",
                        DenialReason: "test")),
                timeout.Token);

            _ = await connected.Task.WaitAsync(timeout.Token);
            Assert.True(registry.IsAvailable);

            await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));
            await readTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(registry.IsAvailable);
        }

        await using (var replacementServer = new BrokerPipeServer(options))
        {
            var replacementConnected =
                new TaskCompletionSource<BrokerPipeConnection>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementAccept = replacementServer.AcceptOnceAsync(
                connection =>
                {
                    registry.Set(connection);
                    replacementConnected.TrySetResult(connection);
                },
                registry.Clear,
                timeout.Token);
            await using var replacementClient =
                await BrokerPipeClientConnection.ConnectAsync(
                    new BrokerPipeClientOptions(
                        pipeName,
                        brokerPolicy,
                        servicePolicy));
            var replacementRead = replacementClient.RunAsync(
                (request, _) => Task.FromResult(
                    new BrokerCredentialPromptResult(
                        request.RequestId,
                        request.NonceB64Url,
                        Confirmed: false,
                        Credential: null,
                        CredentialType: "password",
                        DenialReason: "test")),
                timeout.Token);

            _ = await replacementConnected.Task.WaitAsync(timeout.Token);
            Assert.True(registry.IsAvailable);

            timeout.Cancel();
            try
            {
                await replacementRead;
            }
            catch (OperationCanceledException)
            {
            }
            await replacementAccept;
        }
    }

    [Fact]
    public async Task SilentBrokerHeartbeatReleasesRegistration()
    {
        var pipeName = "CipherVault.Broker.HeartbeatTest." +
            Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var imagePath = Environment.ProcessPath!;
        var imageHash = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var brokerPolicy = Policy(
            VerifiedProcessKind.AuthBroker,
            currentUser.Value,
            currentSession,
            imagePath,
            imageHash);
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var options = new BrokerPipeServerOptions(
            pipeName,
            PipeSecurityFactory.Create(
                [currentUser],
                includeAdministrators: false,
                includeSystem: false),
            Guid.NewGuid().ToString("D"),
            "1.0.0-test",
            serviceIdentity,
            [brokerPolicy],
            currentUser.Value,
            currentUser.Value,
            TimeSpan.FromMinutes(5),
            RequireInteractiveSession: false,
            ActivityProvider: new BrokerActivityProvider(),
            HeartbeatTimeout: TimeSpan.FromSeconds(1));
        var registry = new BrokerConnectionRegistry();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new BrokerPipeServer(options);
        var connected = new TaskCompletionSource<BrokerPipeConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptTask = server.AcceptOnceAsync(
            connection =>
            {
                registry.Set(connection);
                connected.TrySetResult(connection);
            },
            registry.Clear,
            timeout.Token);
        await using var client = await BrokerPipeClientConnection.ConnectAsync(
            new BrokerPipeClientOptions(
                pipeName,
                brokerPolicy,
                servicePolicy,
                ActivityReportInterval: TimeSpan.FromSeconds(10)));
        var readTask = client.RunAsync(
            (request, _) => Task.FromResult(
                new BrokerCredentialPromptResult(
                    request.RequestId,
                    request.NonceB64Url,
                    Confirmed: false,
                    Credential: null,
                    CredentialType: "password",
                    DenialReason: "test")),
            timeout.Token);

        _ = await connected.Task.WaitAsync(timeout.Token);
        Assert.True(registry.IsAvailable);

        await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));
        await readTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(registry.IsAvailable);
    }

    [Fact]
    public async Task AcceptLoopRetriesCreationAndRecoversAfterBrokerKill()
    {
        var pipeName = "CipherVault.Broker.ProcessRecoveryTest." +
            Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentSession = (uint)Process.GetCurrentProcess().SessionId;
        var serviceImagePath = Environment.ProcessPath!;
        var brokerImagePath = FindTestBrokerExecutable();
        var servicePolicy = Policy(
            VerifiedProcessKind.HostService,
            currentUser.Value,
            currentSession,
            serviceImagePath,
            WindowsProcessIdentityVerifier.ComputeFileSha256(serviceImagePath));
        var brokerPolicy = Policy(
            VerifiedProcessKind.AuthBroker,
            currentUser.Value,
            currentSession,
            brokerImagePath,
            WindowsProcessIdentityVerifier.ComputeFileSha256(brokerImagePath));
        var serviceIdentity = VerifyIdentity(
            (uint)Environment.ProcessId,
            servicePolicy);
        var security = PipeSecurityFactory.Create(
            [currentUser],
            includeAdministrators: false,
            includeSystem: false);
        var options = new BrokerPipeServerOptions(
            pipeName,
            security,
            Guid.NewGuid().ToString("D"),
            "1.0.0-test",
            serviceIdentity,
            [brokerPolicy],
            currentUser.Value,
            currentUser.Value,
            TimeSpan.FromMinutes(1),
            RequireInteractiveSession: false,
            ActivityProvider: new BrokerActivityProvider(),
            HeartbeatTimeout: TimeSpan.FromSeconds(2));
        var registry = new BrokerConnectionRegistry();
        var activityProvider = new BrokerActivityProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var blocker = NamedPipeServerFactory.Create(pipeName, security);
        var loopTask = new BrokerPipeAcceptLoop(
                options,
                registry,
                activityProvider,
                NullLogger<BrokerPipeAcceptLoop>.Instance)
            .RunAsync(timeout.Token);
        var optionsPath = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-test-broker-{Guid.NewGuid():N}.json");
        Process? first = null;
        Process? second = null;

        try
        {
            await Task.Delay(350, timeout.Token);
            await blocker.DisposeAsync();

            var clientOptions = new BrokerPipeClientOptions(
                pipeName,
                brokerPolicy,
                servicePolicy,
                ActivityReportInterval: TimeSpan.FromMilliseconds(500));
            await File.WriteAllTextAsync(
                optionsPath,
                JsonSerializer.Serialize(
                    clientOptions,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                timeout.Token);

            first = StartTestBroker(optionsPath);
            await WaitUntilAsync(
                () => RegisteredProcessId(registry) == (uint?)first.Id,
                TimeSpan.FromSeconds(10));

            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync(timeout.Token);
            await WaitUntilAsync(
                () => !registry.IsAvailable,
                TimeSpan.FromSeconds(10));

            second = StartTestBroker(optionsPath);
            await WaitUntilAsync(
                () => RegisteredProcessId(registry) == (uint?)second.Id,
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var process in new[] { first, second })
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
                    exception is InvalidOperationException or Win32Exception)
                {
                }
                process.Dispose();
            }
            timeout.Cancel();
            try
            {
                await loopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            await blocker.DisposeAsync();
            File.Delete(optionsPath);
        }
    }

    [Fact]
    public void ClientPolicyFactoryBuildsBrokerAndHostPolicies()
    {
        const string sha256 =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string thumbprint =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var manifest = new CipherVaultInstallPublicManifest
        {
            Format = CipherVaultServiceConfiguration.InstallManifestFormat,
            FormatVersion =
                CipherVaultServiceConfiguration.InstallManifestFormatVersion,
            InstallId = Guid.NewGuid(),
            ProductVersion = "1.0.0",
            ServiceName = "CipherVaultHost",
            ServiceAccount = @"NT SERVICE\CipherVaultHost",
            ServiceSid = "S-1-5-80-1",
            OwnerSid = "S-1-5-21-1",
            Pipes = new CipherVaultPipeConfigFile
            {
                Admin = "CipherVault.Admin.v1",
                Broker = "CipherVault.Broker.v1",
                Bridge = "CipherVault.Bridge.v1",
                RejectRemoteClients = true
            },
            AllowedServerImages =
            [
                Image(
                    "HostService",
                    @"C:\Program Files\CipherVault\CipherVault.Host.exe",
                    sha256,
                    thumbprint)
            ],
            AllowedClientImages =
            [
                Image(
                    "AdminUi",
                    @"C:\Program Files\CipherVault\CipherVault.Ui.Wpf.exe",
                    sha256,
                    thumbprint),
                Image(
                    "AuthBroker",
                    @"C:\Program Files\CipherVault\CipherVault.Auth.Broker.exe",
                    sha256,
                    thumbprint)
            ]
        };

        var options = StrictServiceClientPolicyFactory.CreateBrokerOptions(
            manifest,
            manifest.Pipes.Broker!,
            manifest.OwnerSid!,
            ownerSessionId: 7);

        Assert.Equal(VerifiedProcessKind.AuthBroker, options.ClientPolicy.ExpectedKind);
        Assert.Equal(7u, options.ClientPolicy.ExpectedSessionId);
        Assert.Equal(VerifiedProcessKind.HostService, options.ServerPolicy.ExpectedKind);
        Assert.Equal("S-1-5-80-1", options.ServerPolicy.ExpectedUserSid);
        Assert.Equal(0u, options.ServerPolicy.ExpectedSessionId);

        var adminOptions =
            StrictServiceClientPolicyFactory.CreateAdminOptions(
                manifest,
                "CipherVault.Admin.v1",
                manifest.OwnerSid!,
                ownerSessionId: 7,
                VerifiedProcessKind.AdminUi);
        Assert.Equal(
            VerifiedProcessKind.AdminUi,
            adminOptions.ClientPolicy.ExpectedKind);
        Assert.Equal(
            VerifiedProcessKind.HostService,
            adminOptions.ServerPolicy.ExpectedKind);
    }

    private static ProcessVerificationPolicy Policy(
        VerifiedProcessKind kind,
        string userSid,
        uint sessionId,
        string imagePath,
        string imageHash)
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
                    imageHash,
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

    private static async Task WriteInvalidHandshakeAsync(string pipeName)
    {
        await using var raw = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await raw.ConnectAsync(5000);
        var payload = Encoding.UTF8.GetBytes("{}");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await raw.WriteAsync(length);
        await raw.WriteAsync(payload);
        await raw.FlushAsync();
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

    private static Process StartTestBroker(string optionsPath)
    {
        var startInfo = new ProcessStartInfo(FindTestBrokerExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(optionsPath);
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
                    "Broker test condition was not reached.");
            }
            await Task.Delay(25);
        }
    }

    private static CipherVaultImagePolicyFile Image(
        string kind,
        string path,
        string sha256,
        string thumbprint)
    {
        return new CipherVaultImagePolicyFile
        {
            Kind = kind,
            Path = path,
            Sha256 = sha256,
            SignerThumbprint = thumbprint,
            SignerSubject = "CN=CipherVault Test"
        };
    }
}
