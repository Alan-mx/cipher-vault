using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Host.Security;
using CipherVault.Host.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

try
{
    if (ShouldRunAsService(args))
    {
        await RunAsServiceAsync(args);
    }
    else
    {
        throw new VaultException(
            VaultErrorCode.InvalidInput,
            "CipherVault.Host must run as the strict Windows Service.");
    }
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

static bool ShouldRunAsService(string[] arguments)
{
    return WindowsServiceHelpers.IsWindowsService() ||
        arguments.Any(argument =>
            string.Equals(argument, "--service", StringComparison.OrdinalIgnoreCase));
}

static async Task RunAsServiceAsync(string[] arguments)
{
    var paths = CipherVaultServicePaths.Default;
    var configPath = GetOption(arguments, "--config") ?? paths.ServiceConfigPath;
    var configuration = CipherVaultServiceConfiguration.Load(configPath);
    await using var service = StrictServiceHost.Create(configuration, paths);
    await service.RunAsync();
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
