using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class HostClientSecurityTests
{
    [Fact]
    public async Task IdentityProbeDoesNotSendAdminTokenWhenFingerprintDoesNotMatch()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-host-client-{Guid.NewGuid():N}");
        var vaultPath = Path.Combine(root, "vault");
        var configPath = Path.Combine(root, "config.json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var configuration = new ResolvedCipherVaultHostConfig(
                vaultPath,
                VaultSessionOptions.Default,
                new HostEndpointOptions(
                    "127.0.0.1",
                    port,
                    "/mcp",
                    "/admin/v1",
                    4 * 1024 * 1024,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                [],
                configPath);
            var expectedFingerprint = HostConfigurationFingerprint.Compute(
                configuration.VaultPath,
                configuration.ConfigPath,
                configuration.Endpoint.BindAddress,
                configuration.Endpoint.Port,
                configuration.Endpoint.McpPath,
                configuration.Endpoint.AdminPath);
            var wrongFingerprint = expectedFingerprint == new string('0', 64)
                ? new string('1', 64)
                : new string('0', 64);
            var serverTask = ServeIdentityOnceAsync(
                listener,
                wrongFingerprint,
                timeout.Token);

            var exception = await Assert.ThrowsAsync<VaultException>(() =>
                HttpHostVaultClient.CreateAsync(
                    configuration,
                    HostAdminClientKind.Ui,
                    timeout.Token));
            Assert.Equal(VaultErrorCode.Conflict, exception.Code);

            var request = await serverTask;
            Assert.Equal("GET", request.Method);
            Assert.Equal("/admin/v1/identity", request.Path);
            Assert.Null(request.Authorization);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<CapturedRequest> ServeIdentityOnceAsync(
        TcpListener listener,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken)
            ?? throw new InvalidOperationException("The identity request was empty.");
        var requestParts = requestLine.Split(' ');
        if (requestParts.Length < 2)
        {
            throw new InvalidOperationException(
                $"The identity request line was invalid: {requestLine}");
        }

        string? authorization = null;
        while (true)
        {
            var headerLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(headerLine))
            {
                break;
            }
            var separator = headerLine.IndexOf(':');
            if (separator > 0 &&
                headerLine[..separator].Equals(
                    "Authorization",
                    StringComparison.OrdinalIgnoreCase))
            {
                authorization = headerLine[(separator + 1)..].Trim();
            }
        }

        var payload = new HostApiResponse<HostHealthData>(
            true,
            new HostHealthData(
                "ready",
                "1.0.0",
                "1",
                Guid.NewGuid().ToString("D"),
                fingerprint));
        var body = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var headers = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return new CapturedRequest(
            requestParts[0],
            requestParts[1],
            authorization);
    }

    private sealed record CapturedRequest(
        string Method,
        string Path,
        string? Authorization);
}
