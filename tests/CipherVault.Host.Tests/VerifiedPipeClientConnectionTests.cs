using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Ipc;
using CipherVault.Security.Win;

namespace CipherVault.Host.Tests;

public sealed class VerifiedPipeClientConnectionTests
{
    [Fact]
    public async Task ClientVerifiesActualServerProcessAndHash()
    {
        var pipeName = "CipherVault.Test." + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var serverWait = server.WaitForConnectionAsync();
        var policy = CurrentProcessServerPolicy();

        await using var client = await VerifiedPipeClientConnection.ConnectAsync(
            pipeName,
            policy);
        await serverWait;

        Assert.Equal(Environment.ProcessPath, client.ServerIdentity.FinalImagePath);
        Assert.Equal(
            (uint)Environment.ProcessId,
            client.ServerIdentity.ProcessId);
        Assert.Equal(
            WindowsIdentity.GetCurrent().User!.Value,
            client.ServerIdentity.UserSid);
        Assert.Equal(
            (uint)Process.GetCurrentProcess().SessionId,
            client.ServerIdentity.SessionId);
    }

    [Fact]
    public async Task ClientRejectsServerWithWrongImageHash()
    {
        var pipeName = "CipherVault.Test." + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        var serverWait = server.WaitForConnectionAsync();
        var policy = CurrentProcessServerPolicy() with
        {
            AllowedImages =
            [
                new ProcessImageIdentity(
                    VerifiedProcessKind.HostService,
                    Environment.ProcessPath!,
                    new string('0', 64),
                    SignerThumbprint: null,
                    SignerSubject: null)
            ]
        };

        var exception = await Assert.ThrowsAsync<ProcessIdentityRejectedException>(
            () => VerifiedPipeClientConnection.ConnectAsync(pipeName, policy));
        await serverWait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessVerificationStatus.HashInvalid, exception.Status);
    }

    [Fact]
    public void InstallManifestBuildsHostServicePolicy()
    {
        var imagePath = Environment.ProcessPath!;
        var manifest = new CipherVaultInstallPublicManifest
        {
            Format = CipherVaultServiceConfiguration.InstallManifestFormat,
            FormatVersion = 1,
            InstallId = Guid.NewGuid(),
            ProductVersion = "1.0.0",
            ServiceName = "CipherVaultHost",
            ServiceAccount = "NT SERVICE\\CipherVaultHost",
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
                new CipherVaultImagePolicyFile
                {
                    Kind = "host",
                    Path = imagePath,
                    Sha256 = WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath),
                    SignerThumbprint = new string('a', 64)
                }
            ]
        };

        var policy = InstallManifestPolicyFactory.CreateServerPolicy(manifest);

        Assert.Equal(VerifiedProcessKind.HostService, policy.ExpectedKind);
        Assert.Equal("S-1-5-80-1", policy.ExpectedUserSid);
        Assert.Equal(0u, policy.ExpectedSessionId);
        Assert.True(policy.RequireSignature);
        Assert.Single(policy.AllowedImages);
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        var security = PipeSecurityFactory.Create(
            [WindowsIdentity.GetCurrent().User!],
            includeAdministrators: false,
            includeSystem: false);
        return NamedPipeServerFactory.Create(pipeName, security);
    }

    private static ProcessVerificationPolicy CurrentProcessServerPolicy()
    {
        var imagePath = Environment.ProcessPath!;
        return new ProcessVerificationPolicy(
            VerifiedProcessKind.HostService,
            WindowsIdentity.GetCurrent().User!.Value,
            ExpectedLogonSid: null,
            (uint)Process.GetCurrentProcess().SessionId,
            [
                new ProcessImageIdentity(
                    VerifiedProcessKind.HostService,
                    imagePath,
                    WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath),
                    SignerThumbprint: null,
                    SignerSubject: null)
            ],
            AllowedSignerThumbprints: [],
            RequireSha256: true,
            RequireSignature: false);
    }
}
