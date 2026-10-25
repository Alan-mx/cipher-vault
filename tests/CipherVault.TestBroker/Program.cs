using System.Text.Json;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;

var approvalDelay = TimeSpan.Zero;
if (int.TryParse(
        Environment.GetEnvironmentVariable(
            "CIPHER_VAULT_TEST_APPROVAL_DELAY_MS"),
        out var approvalDelayMilliseconds) &&
    approvalDelayMilliseconds > 0)
{
    approvalDelay = TimeSpan.FromMilliseconds(approvalDelayMilliseconds);
}
var approvalStartedPath = Environment.GetEnvironmentVariable(
    "CIPHER_VAULT_TEST_APPROVAL_STARTED_FILE");

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
            ApproveAsync,
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

async Task<BrokerApprovalDecision> ApproveAsync(
    BrokerApprovalRequest request,
    CancellationToken cancellationToken)
{
    if (!string.IsNullOrWhiteSpace(approvalStartedPath))
    {
        await WriteMarkerAtomicallyAsync(
            approvalStartedPath,
            request.ApprovalId.ToString("D"),
            cancellationToken);
    }
    if (approvalDelay > TimeSpan.Zero)
    {
        await Task.Delay(approvalDelay, cancellationToken);
    }
    return new BrokerApprovalDecision(
        request.ApprovalId,
        request.NonceB64Url,
        Approved: true,
        DenialReason: null,
        ApprovedScopes: request.RequestedScopes);
}

static async Task WriteMarkerAtomicallyAsync(
    string path,
    string content,
    CancellationToken cancellationToken)
{
    var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
    try
    {
        await File.WriteAllTextAsync(
            temporaryPath,
            content,
            cancellationToken);
        File.Move(temporaryPath, path);
    }
    finally
    {
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }
}
