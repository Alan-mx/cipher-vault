using System.Text.Json;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;

if (args.Length != 1)
{
    Console.Error.WriteLine("Expected a Broker client options JSON file.");
    return 2;
}

BrokerPipeClientOptions options;
try
{
    var json = await File.ReadAllTextAsync(args[0]);
    options = JsonSerializer.Deserialize<BrokerPipeClientOptions>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("Broker client options were empty.");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"options_invalid: {exception.Message}");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArguments) =>
{
    eventArguments.Cancel = true;
    cancellation.Cancel();
};

while (!cancellation.IsCancellationRequested)
{
    try
    {
        await using var connection =
            await BrokerPipeClientConnection.ConnectAsync(
                options,
                cancellation.Token);
        Console.Out.WriteLine($"connected:{Environment.ProcessId}");
        await connection.RunAsync(
            (request, _) => Task.FromResult(
                new BrokerCredentialPromptResult(
                    request.RequestId,
                    request.NonceB64Url,
                    Confirmed: false,
                    Credential: null,
                    CredentialType: "password",
                    DenialReason: "test")),
            (request, _) => Task.FromResult(
                new BrokerApprovalDecision(
                    request.ApprovalId,
                    request.NonceB64Url,
                    Approved: true,
                    DenialReason: null,
                    ApprovedScopes: request.RequestedScopes)),
            cancellation.Token);
    }
    catch (OperationCanceledException) when (
        cancellation.IsCancellationRequested)
    {
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"connection_failed: {exception.GetType().Name}: {exception.Message}");
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }
}

return 0;
