using System.Diagnostics;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CipherVault.Mcp.Tests;

public sealed class McpStartupTests
{
    [Fact]
    public async Task UnavailableBridgeExitsWithStableError()
    {
        var manifestPath = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-mcp-startup-{Guid.NewGuid():N}.json");
        var dotnetHost = FindDotnetHost();
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(CreateManifest(dotnetHost)));

        try
        {
            var startInfo = new ProcessStartInfo(dotnetHost)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(FindMcpAssembly());
            startInfo.ArgumentList.Add("--manifest");
            startInfo.ArgumentList.Add(manifestPath);
            startInfo.ArgumentList.Add("--scopes");
            startInfo.ArgumentList.Add("read");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Unable to start CipherVault.Mcp.exe.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.StandardInput.Close();

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException(
                    "CipherVault.Mcp.exe did not exit after the Bridge timeout.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.Equal(1, process.ExitCode);
            Assert.Equal(string.Empty, stdout);
            // The manifest has no Auth Broker image, so the best-effort Broker
            // launch reports a warning instead of aborting startup.
            Assert.Contains("broker_launch_failed:", stderr);
            Assert.Contains("bridge_unavailable:", stderr);
            Assert.DoesNotContain("Unhandled exception", stderr);
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    private static object CreateManifest(string dotnetHost)
    {
        var ownerSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException(
                "Unable to resolve the current Windows user.");
        var imageHash = ComputeFileSha256(dotnetHost);
        var signerThumbprint = ReadSignerSha256(dotnetHost);

        return new
        {
            format = "cipher-vault-install",
            formatVersion = 1,
            installId = Guid.NewGuid(),
            productVersion = "1.0.0",
            serviceName = "CipherVaultHost",
            serviceAccount = @"NT SERVICE\CipherVaultHost",
            serviceSid = "S-1-5-80-1",
            ownerSid,
            pipes = new
            {
                admin = "CipherVault.Mcp.Test.Admin",
                broker = "CipherVault.Mcp.Test.Broker",
                bridge = "CipherVault.Mcp.Test.Bridge." +
                    Guid.NewGuid().ToString("N"),
                rejectRemoteClients = true
            },
            allowedServerImages = new[]
            {
                Image(
                    "HostService",
                    dotnetHost,
                    imageHash,
                    signerThumbprint)
            },
            allowedClientImages = new[]
            {
                Image(
                    "McpBridge",
                    dotnetHost,
                    imageHash,
                    signerThumbprint)
            }
        };
    }

    private static object Image(
        string kind,
        string path,
        string sha256,
        string signerThumbprint)
    {
        return new
        {
            kind,
            path,
            sha256,
            signerThumbprint,
            signerSubject = "CN=CipherVault Test"
        };
    }

    private static string FindDotnetHost()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT") ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet");
        var executable = Path.Combine(root, "dotnet.exe");
        Assert.True(
            File.Exists(executable),
            $"dotnet host was not found: {executable}");
        return executable;
    }

    private static string FindMcpAssembly()
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
        var assembly = Path.Combine(
            repositoryRoot,
            "src",
            "CipherVault.Mcp",
            "bin",
            configuration,
            "net10.0-windows",
            "CipherVault.Mcp.dll");
        Assert.True(
            File.Exists(assembly),
            $"MCP assembly was not found: {assembly}");
        return assembly;
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ReadSignerSha256(string path)
    {
#pragma warning disable SYSLIB0057
        var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        using var certificate = X509CertificateLoader.LoadCertificate(
            signer.GetRawCertData());
        return certificate.GetCertHashString(
            HashAlgorithmName.SHA256);
    }
}
