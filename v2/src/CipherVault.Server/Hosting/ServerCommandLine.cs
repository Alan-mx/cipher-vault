namespace CipherVault.Server.Hosting;

public enum ServerRunMode
{
    Normal,
    Restore,
    Invalid
}

public sealed record RestoreCommandOptions(
    string BackupFile,
    string TargetDirectory);

public sealed record ServerCommandLine(
    ServerRunMode Mode,
    RestoreCommandOptions? RestoreOptions,
    string? ErrorCode)
{
    public static ServerCommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            return new ServerCommandLine(
                ServerRunMode.Normal,
                null,
                null);
        }

        string? backupFile = null;
        string? target = null;
        for (var index = 0; index < args.Length; index++)
        {
            var flag = args[index];
            if (flag is not ("--restore-backup" or "--target")
                || index + 1 >= args.Length)
            {
                return Invalid();
            }

            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value)
                || value.StartsWith("--", StringComparison.Ordinal))
            {
                return Invalid();
            }

            if (flag == "--restore-backup")
            {
                if (backupFile is not null)
                {
                    return Invalid();
                }
                backupFile = value;
            }
            else
            {
                if (target is not null)
                {
                    return Invalid();
                }
                target = value;
            }
        }

        if (backupFile is null || target is null)
        {
            return Invalid();
        }

        return new ServerCommandLine(
            ServerRunMode.Restore,
            new RestoreCommandOptions(backupFile, target),
            null);
    }

    private static ServerCommandLine Invalid() =>
        new(
            ServerRunMode.Invalid,
            null,
            "restore_arguments_invalid");
}
