using CipherVault.Core.Errors;

namespace CipherVault.Application.Vault;

internal sealed class VaultInitializationScope
{
    private readonly HashSet<string> createdArtifacts =
        new(StringComparer.OrdinalIgnoreCase);

    internal VaultInitializationScope(VaultInstancePaths target, string stagingDirectory)
    {
        Target = target;
        StagingDirectory = stagingDirectory;
    }

    internal VaultInstancePaths Target { get; }

    internal string StagingDirectory { get; }

    internal bool Succeeded { get; private set; }

    internal void MarkCreated(string path)
    {
        createdArtifacts.Add(path);
    }

    internal void MarkSucceeded()
    {
        Succeeded = true;
    }

    internal void Cleanup()
    {
        if (Directory.Exists(StagingDirectory))
        {
            try
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Unable to clean the Vault initialization staging directory.");
            }
        }
    }
}
