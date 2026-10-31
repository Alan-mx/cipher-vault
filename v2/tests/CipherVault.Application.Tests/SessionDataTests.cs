using CipherVault.Application.Vault;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class SessionDataTests
{
    [Fact]
    public void DefaultSessionOptionsUseTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), VaultSessionOptions.Default.IdleTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void SessionOptionsRejectValuesOutsideRange(int minutes)
    {
        var exception = Assert.Throws<VaultException>(
            () => new VaultSessionOptions(TimeSpan.FromMinutes(minutes)));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void TimingOptionsExposeFixedProductionDefaults()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), VaultSessionTimingOptions.Default.CancelGrace);
        Assert.Equal(TimeSpan.FromSeconds(5), VaultSessionTimingOptions.Default.InterruptGrace);
        Assert.Equal(TimeSpan.FromSeconds(15), VaultSessionTimingOptions.Default.TotalLockDeadline);
    }

    [Fact]
    public void ApplicationProjectDoesNotReferenceSqliteAspNetOrMcp()
    {
        var root = FindV2Root();
        var project = File.ReadAllText(Path.Combine(
            root,
            "src",
            "CipherVault.Application",
            "CipherVault.Application.csproj"));

        Assert.DoesNotContain("Microsoft.Data.Sqlite", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite3MC", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.AspNetCore", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ModelContextProtocol", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplicationSourceDoesNotReferenceDatabaseOrHostingTypes()
    {
        var root = FindV2Root();
        var sourceRoot = Path.Combine(root, "src", "CipherVault.Application");
        var forbidden = new[]
        {
            "Microsoft.Data.Sqlite",
            "SQLitePCL",
            "Microsoft.AspNetCore",
            "ModelContextProtocol",
            "SqliteConnection",
            "SqliteCommand",
            "SqliteDataReader",
            "SqliteTransaction"
        };
        var files = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            foreach (var value in forbidden)
            {
                Assert.DoesNotContain(value, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void RuntimeUsesPublicHandleInterruptWithoutNoOpCancel()
    {
        var root = FindV2Root();
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "src",
            "CipherVault.Core",
            "Database",
            "VaultDatabaseRuntime.cs"));

        Assert.Contains(
            "raw.sqlite3_interrupt(session.Connection.Handle)",
            runtime,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteCommand.Cancel", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain(".Command.Cancel", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerDoesNotCallProcessShutdown()
    {
        var root = FindV2Root();
        var manager = File.ReadAllText(Path.Combine(
            root,
            "src",
            "CipherVault.Application",
            "Vault",
            "VaultSessionManager.cs"));

        Assert.DoesNotContain("sqlite3_shutdown", manager, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ShutdownProcess", manager, StringComparison.Ordinal);
    }

    private static string FindV2Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CipherVault.V2.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("V2 solution root was not found.");
    }
}
