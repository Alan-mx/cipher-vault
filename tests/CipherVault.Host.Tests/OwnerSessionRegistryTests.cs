using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Ipc;

namespace CipherVault.Host.Tests;

public sealed class OwnerSessionRegistryTests
{
    [Fact]
    public void ConcurrentProcessesMustShareOwnerLogonAndSession()
    {
        var registry = new OwnerSessionRegistry();
        using var first = registry.Enter(CreateIdentity(logonSuffix: "1"));
        using var matching = registry.Enter(CreateIdentity(logonSuffix: "1"));

        var exception = Assert.Throws<ProcessIdentityRejectedException>(
            () => registry.Enter(CreateIdentity(logonSuffix: "2")));

        Assert.Equal(
            ProcessVerificationStatus.IdentityMismatch,
            exception.Status);
        Assert.Equal("owner_session_mismatch", exception.ReasonCode);
    }

    [Fact]
    public void DifferentInteractiveSessionIsRejected()
    {
        var registry = new OwnerSessionRegistry();
        using var first = registry.Enter(CreateIdentity(sessionId: 7));

        var exception = Assert.Throws<ProcessIdentityRejectedException>(
            () => registry.Enter(CreateIdentity(sessionId: 8)));

        Assert.Equal("owner_session_mismatch", exception.ReasonCode);
    }

    [Fact]
    public void BindingCanChangeAfterAllLeasesEnd()
    {
        var registry = new OwnerSessionRegistry();
        using (registry.Enter(CreateIdentity(logonSuffix: "1", sessionId: 7)))
        {
        }

        using var nextLogin = registry.Enter(
            CreateIdentity(logonSuffix: "2", sessionId: 8));
    }

    private static VerifiedProcessIdentity CreateIdentity(
        string logonSuffix = "1",
        uint sessionId = 7)
    {
        return new VerifiedProcessIdentity(
            VerifiedProcessKind.AdminCli,
            42,
            DateTimeOffset.UtcNow,
            "S-1-5-21-1",
            $"S-1-5-5-1-{logonSuffix}",
            sessionId,
            @"C:\Program Files\CipherVault\CipherVault.Cli.exe",
            new string('a', 64),
            new string('b', 64),
            "CN=CipherVault Test",
            IntegrityLevel: 0x2000);
    }
}
