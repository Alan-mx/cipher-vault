using System.Diagnostics;
using System.Security.Principal;
using CipherVault.Host.Contracts;
using CipherVault.Security.Win;

namespace CipherVault.Host.Tests;

public sealed class WindowsProcessIdentityVerifierTests
{
    [Fact]
    public void CurrentProcessIdentityIsVerified()
    {
        var imagePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current process path is unavailable.");
        var policy = CurrentPolicy(ExpectedSha256(imagePath));

        var result = WindowsProcessIdentityVerifier.Verify(
            (uint)Environment.ProcessId,
            policy);
        using var evidence = result.Evidence;

        Assert.Equal(ProcessVerificationStatus.Verified, result.Status);
        Assert.NotNull(evidence);
        Assert.Equal(imagePath, evidence.Identity.FinalImagePath);
        Assert.Equal(ExpectedSha256(imagePath), evidence.Identity.ImageSha256);
        Assert.Equal(
            WindowsIdentity.GetCurrent().User!.Value,
            evidence.Identity.UserSid);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Identity.LogonSid));
        Assert.Equal(
            (uint)Process.GetCurrentProcess().SessionId,
            evidence.Identity.SessionId);
    }

    [Fact]
    public void WrongSessionIsRejected()
    {
        var imagePath = Environment.ProcessPath!;
        var policy = CurrentPolicy(ExpectedSha256(imagePath)) with
        {
            ExpectedSessionId =
                (uint)Process.GetCurrentProcess().SessionId + 1
        };

        var result = WindowsProcessIdentityVerifier.Verify(
            (uint)Environment.ProcessId,
            policy);

        Assert.Equal(ProcessVerificationStatus.IdentityMismatch, result.Status);
        Assert.Equal("session_id_mismatch", result.ReasonCode);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public void WrongImageHashIsRejected()
    {
        var imagePath = Environment.ProcessPath!;
        var policy = CurrentPolicy(new string('0', 64));

        var result = WindowsProcessIdentityVerifier.Verify(
            (uint)Environment.ProcessId,
            policy);

        Assert.Equal(ProcessVerificationStatus.HashInvalid, result.Status);
        Assert.Equal("image_mismatch", result.ReasonCode);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public void WrongUserIsRejected()
    {
        var imagePath = Environment.ProcessPath!;
        var usersSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinUsersSid,
            null).Value;
        var policy = CurrentPolicy(ExpectedSha256(imagePath)) with
        {
            ExpectedUserSid = usersSid
        };

        var result = WindowsProcessIdentityVerifier.Verify(
            (uint)Environment.ProcessId,
            policy);

        Assert.Equal(ProcessVerificationStatus.IdentityMismatch, result.Status);
        Assert.Equal("user_sid_mismatch", result.ReasonCode);
        Assert.Null(result.Evidence);
    }

    private static ProcessVerificationPolicy CurrentPolicy(string sha256)
    {
        var imagePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current process path is unavailable.");
        return new ProcessVerificationPolicy(
            VerifiedProcessKind.AdminUi,
            WindowsIdentity.GetCurrent().User!.Value,
            ExpectedLogonSid: null,
            (uint)Process.GetCurrentProcess().SessionId,
            [
                new ProcessImageIdentity(
                    VerifiedProcessKind.AdminUi,
                    imagePath,
                    sha256,
                    SignerThumbprint: null,
                    SignerSubject: null)
            ],
            AllowedSignerThumbprints: [],
            RequireSha256: true,
            RequireSignature: false);
    }

    private static string ExpectedSha256(string imagePath)
    {
        return WindowsProcessIdentityVerifier.ComputeFileSha256(imagePath);
    }
}
