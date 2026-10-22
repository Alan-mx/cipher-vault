using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using CipherVault.Host.Contracts;
using CipherVault.Ipc;

namespace CipherVault.Host.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public async Task NamedPipeJsonRoundTripUsesExplicitAcl()
    {
        var pipeName = "CipherVault.Test." + Guid.NewGuid().ToString("N");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var security = PipeSecurityFactory.Create(
            [currentUser],
            includeAdministrators: false,
            includeSystem: false);
        using var server = NamedPipeServerFactory.Create(pipeName, security);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        var connectTask = client.ConnectAsync(5_000);
        await server.WaitForConnectionAsync();
        await connectTask;
        await using var serverConnection = new PipeJsonConnection(server);
        await using var clientConnection = new PipeJsonConnection(client);

        var request = new PipeHelloRequest(
            PipeProtocol.Version,
            PipeNonce.Create());
        await clientConnection.WriteAsync(request);
        var received = await serverConnection.ReadAsync<PipeHelloRequest>();

        Assert.Equal(request, received);

        var response = new PipePingResponse(DateTimeOffset.UtcNow);
        await serverConnection.WriteAsync(response);
        var receivedResponse = await clientConnection.ReadAsync<PipePingResponse>();
        Assert.Equal(response, receivedResponse);
    }

    [Fact]
    public void SecondFirstInstancePipeIsRejected()
    {
        var pipeName = "CipherVault.Test." + Guid.NewGuid().ToString("N");
        var security = PipeSecurityFactory.Create(
            [WindowsIdentity.GetCurrent().User!],
            includeAdministrators: false,
            includeSystem: false);
        using var first = NamedPipeServerFactory.Create(pipeName, security);

        var exception = Assert.Throws<IOException>(
            () => NamedPipeServerFactory.Create(pipeName, security));

        Assert.Contains("Win32 error", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforeAllocation()
    {
        await using var stream = new MemoryStream();
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(
            length,
            PipeProtocol.MaxMessageBytes + 1);
        await stream.WriteAsync(length);
        stream.Position = 0;
        await using var connection = new PipeJsonConnection(stream);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await connection.ReadAsync<PipeHelloRequest>());
    }

    [Fact]
    public void TranscriptBindsNonceAndBothProcessIdentities()
    {
        var now = DateTimeOffset.Parse("2026-10-22T10:00:00Z");
        var client = Identity(VerifiedProcessKind.AdminUi, 10, now, "a");
        var server = Identity(VerifiedProcessKind.HostService, 20, now, "b");
        var clientNonce = PipeNonce.Create();
        var serverNonce = PipeNonce.Create();

        var first = PipeHandshakeTranscript.Compute(
            "CipherVault.Admin.v1",
            PipeProtocol.Version,
            clientNonce,
            serverNonce,
            client,
            server);
        var second = PipeHandshakeTranscript.Compute(
            "CipherVault.Admin.v1",
            PipeProtocol.Version,
            clientNonce,
            serverNonce,
            client,
            server);
        var changed = PipeHandshakeTranscript.Compute(
            "CipherVault.Admin.v1",
            PipeProtocol.Version,
            clientNonce,
            PipeNonce.Create(),
            client,
            server);

        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
        Assert.Equal(43, first.Length);
    }

    private static VerifiedProcessIdentity Identity(
        VerifiedProcessKind kind,
        uint processId,
        DateTimeOffset startedAt,
        string suffix)
    {
        return new VerifiedProcessIdentity(
            kind,
            processId,
            startedAt,
            "S-1-5-21-user",
            "S-1-5-21-logon",
            1,
            $@"C:\CipherVault\{suffix}.exe",
            suffix.PadRight(64, '0'),
            suffix.PadRight(64, '1'),
            "CN=Test",
            IntegrityLevel: 8192);
    }
}
