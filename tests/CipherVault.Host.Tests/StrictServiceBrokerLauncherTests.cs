using System.Diagnostics;
using CipherVault.Host.Client;

namespace CipherVault.Host.Tests;

public sealed class StrictServiceBrokerLauncherTests
{
    [Fact]
    public void StartInfoDoesNotInheritCallerStandardHandles()
    {
        const string executablePath =
            @"C:\Program Files\CipherVault\CipherVault.Auth.Broker.exe";
        const string manifestPath =
            @"C:\ProgramData\CipherVault\install-public.json";

        var startInfo = StrictServiceBrokerLauncher.CreateStartInfo(
            executablePath,
            manifestPath);

        Assert.True(startInfo.UseShellExecute);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.Equal(executablePath, startInfo.FileName);
        Assert.Equal(
            Path.GetDirectoryName(executablePath),
            startInfo.WorkingDirectory);
        Assert.Equal(
            ["--manifest", manifestPath],
            startInfo.ArgumentList);
    }
}
