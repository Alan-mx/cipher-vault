namespace CipherVault.Server.Hosting;

public enum ServerStartupCode
{
    InvalidConfiguration,
    UnsupportedPlatform,
    InstanceConflict,
    StorageFailure,
    DataProtectionFailure
}

public sealed class ServerStartupException : Exception
{
    public ServerStartupException(
        ServerStartupCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public ServerStartupCode Code { get; }
}
