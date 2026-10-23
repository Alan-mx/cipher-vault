using System.Diagnostics;
using System.Security.Principal;
using CipherVault.Host.Contracts;
using CipherVault.Security.Win;

namespace CipherVault.Host.Tests;

public sealed class WindowsProcessAccessGrantTests
{
    [Fact]
    public void GrantedAccessAllowsCurrentProcessIdentityVerification()
    {
        var userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current user SID is unavailable.");
        WindowsProcessAccessGrant.GrantVerificationAccessToCurrentProcess(
            userSid.Value);

        var imagePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Process image path is unavailable.");
        var process = Process.GetCurrentProcess();
        var policy = new ProcessVerificationPolicy(
            VerifiedProcessKind.AdminCli,
            userSid.Value,
            ExpectedLogonSid: null,
            (uint)process.SessionId,
            [
                new ProcessImageIdentity(
                    VerifiedProcessKind.AdminCli,
                    imagePath,
                    WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath),
                    SignerThumbprint: null,
                    SignerSubject: null)
            ],
            AllowedSignerThumbprints: [],
            RequireSha256: true,
            RequireSignature: false);

        var result = WindowsProcessIdentityVerifier.Verify(
            (uint)process.Id,
            policy);
        using var evidence = result.Evidence;

        Assert.Equal(ProcessVerificationStatus.Verified, result.Status);
        Assert.NotNull(evidence);
        Assert.Equal(userSid.Value, evidence.Identity.UserSid);
    }

    [Fact]
    public void RepeatedGrantIsIdempotent()
    {
        var userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current user SID is unavailable.");

        WindowsProcessAccessGrant.GrantVerificationAccessToCurrentProcess(
            userSid.Value);
        WindowsProcessAccessGrant.GrantVerificationAccessToCurrentProcess(
            userSid.Value);
    }

    [Fact]
    public void InvalidSidIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => WindowsProcessAccessGrant.GrantVerificationAccessToCurrentProcess(
                "not-a-sid"));
    }
}
