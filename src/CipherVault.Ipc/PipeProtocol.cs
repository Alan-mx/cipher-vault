namespace CipherVault.Ipc;

public static class PipeProtocol
{
    public const string Version = "1";
    public const int MaxMessageBytes = 4 * 1024 * 1024;
    public const string HelloMethod = "pipe.hello";
    public const string AuthenticateMethod = "pipe.authenticate";
    public const string PingMethod = "pipe.ping";
    public const string CloseMethod = "pipe.close";
}
