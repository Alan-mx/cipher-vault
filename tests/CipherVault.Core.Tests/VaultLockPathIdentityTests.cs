using System.Diagnostics;
using System.Runtime.Versioning;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Core.Tests;

public sealed class VaultLockPathIdentityTests
{
    [Fact]
    public void InjectedAliasResolutionMapsMissingChildToSameIdentity()
    {
        using var fixture = new PathFixture();
        Directory.CreateDirectory(fixture.RealRoot);
        Directory.CreateDirectory(fixture.AliasRoot);

        var aliasPrefix = Path.GetFullPath(fixture.AliasRoot);
        Func<string, string?> resolver = current =>
        {
            var fullPath = Path.GetFullPath(current);
            if (string.Equals(fullPath, aliasPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return fixture.RealRoot;
            }
            if (fullPath.StartsWith(
                    aliasPrefix + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(
                    fixture.RealRoot,
                    Path.GetRelativePath(aliasPrefix, fullPath));
            }
            return fullPath;
        };

        var original = VaultPathIdentity.GetLockIdentity(
            Path.Combine(fixture.RealRoot, "vault"),
            resolver);
        var alias = VaultPathIdentity.GetLockIdentity(
            Path.Combine(fixture.AliasRoot, "vault"),
            resolver);

        Assert.Equal(original, alias);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task JunctionAliasSharesOwnershipAndProcessLocks()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new PathFixture();
        Directory.CreateDirectory(fixture.RealRoot);
        CreateJunction(fixture.AliasRoot, fixture.RealRoot);

        Assert.Equal(
            VaultPathIdentity.GetLockIdentity(fixture.RealRoot),
            VaultPathIdentity.GetLockIdentity(fixture.AliasRoot));

        var ownership = await VaultOwnershipLock.AcquireAsync(fixture.RealRoot);
        try
        {
            var conflict = await Assert.ThrowsAsync<VaultException>(async () =>
                await VaultOwnershipLock.AcquireAsync(fixture.AliasRoot));
            Assert.Equal(VaultErrorCode.Conflict, conflict.Code);
        }
        finally
        {
            await ownership.DisposeAsync();
        }

        using var firstProcessLock = await VaultProcessLock.AcquireAsync(
            fixture.RealRoot);
        var secondProcessLockTask = VaultProcessLock.AcquireAsync(fixture.AliasRoot);

        await Task.Delay(150);
        Assert.False(secondProcessLockTask.IsCompleted);

        firstProcessLock.Dispose();
        using var secondProcessLock = await secondProcessLockTask.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.NotNull(secondProcessLock);
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cmd.exe could not be started.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Junction creation failed: {standardOutput}{standardError}");
        }
    }

    private sealed class PathFixture : IDisposable
    {
        public PathFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-path-identity-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string RealRoot => Path.Combine(Root, "real");
        public string AliasRoot => Path.Combine(Root, "alias");

        public void Dispose()
        {
            if (Directory.Exists(AliasRoot))
            {
                Directory.Delete(AliasRoot, recursive: false);
            }
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
