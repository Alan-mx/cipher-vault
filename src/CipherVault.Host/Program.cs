using System.Net;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Host;
using CipherVault.Host.Security;

try
{
    var configPath = GetOption(args, "--config");
    var configuration = CipherVaultHostConfiguration.Load(
        CipherVaultHostConfigOverrides.Empty,
        configPath);
    await using var ownership = await VaultOwnershipLock.AcquireAsync(
        configuration.VaultPath);
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.ConfigureKestrel(options =>
        options.Listen(IPAddress.Loopback, configuration.Endpoint.Port));
    var app = HostApplication.Configure(
        builder,
        configuration,
        new DpapiAdminTokenStore(),
        new WindowsHostCredentialPrompt());
    await app.RunAsync();
    return 0;
}
catch (VaultException exception)
{
    Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"storage_error: {exception.Message}");
    return 1;
}

static string? GetOption(string[] arguments, string option)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase))
        {
            if (arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"Option {option} requires a value.");
            }
            return arguments[index + 1];
        }
    }
    if (arguments.Any(argument =>
            string.Equals(argument, option, StringComparison.OrdinalIgnoreCase)))
    {
        throw new VaultException(
            VaultErrorCode.InvalidInput,
            $"Option {option} requires a value.");
    }
    return null;
}

public partial class Program
{
}
