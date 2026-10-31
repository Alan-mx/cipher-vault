using System.Security.Cryptography;
using CipherVault.Server.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CipherVault.Server.Security;

public static class DataProtectionConfiguration
{
    public static IServiceCollection AddCipherVaultDataProtection(
        this IServiceCollection services,
        ApplicationPaths paths)
    {
        services.AddDataProtection()
            .SetApplicationName("CipherVault.Server")
            .PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionDirectory));
        return services;
    }
}
