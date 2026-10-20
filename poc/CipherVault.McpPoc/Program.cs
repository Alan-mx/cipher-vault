using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<AuthTools>();

await builder.Build().RunAsync();

[McpServerToolType]
public sealed class AuthTools
{
    private const string ExpectedPassword = "poc-password";

    [McpServerTool(Name = "auth_probe")]
    [Description("Open a Windows desktop password prompt owned by this MCP process and return the user's decision.")]
    public static string AuthProbe()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var result = ShowAuthWindow();
        var payload = new
        {
            approved = result.Approved,
            method = "password",
            processId = Environment.ProcessId,
            startedAt,
            finishedAt = DateTimeOffset.UtcNow
        };

        AppendLog(payload);
        return JsonSerializer.Serialize(payload);
    }

    private static AuthResult ShowAuthWindow()
    {
        var completion = new TaskCompletionSource<AuthResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AppendDiagnostic("auth thread starting");
                var application = new Application
                {
                    ShutdownMode = ShutdownMode.OnLastWindowClose
                };
                var window = new AuthWindow(ExpectedPassword);
                window.Closed += (_, _) => AppendDiagnostic("auth window closed");
                window.Show();
                AppendDiagnostic("auth window Show() returned");
                application.Run();
                completion.SetResult(window.Result);
            }
            catch (Exception exception)
            {
                AppendDiagnostic($"auth thread exception: {exception}");
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "CipherVault.AuthPoc"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var timeoutSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("PV_POC_AUTH_TIMEOUT_SEC"),
            out var parsedTimeout)
            ? parsedTimeout
            : 60;

        if (!completion.Task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            return new AuthResult(false, "timeout");
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    private static void AppendLog(object payload)
    {
        var artifactDir = Path.Combine(
            Environment.CurrentDirectory,
            "poc",
            "artifacts");
        Directory.CreateDirectory(artifactDir);
        File.AppendAllText(
            Path.Combine(artifactDir, "mcp-auth.log"),
            JsonSerializer.Serialize(payload) + Environment.NewLine);
    }

    private static void AppendDiagnostic(string message)
    {
        var artifactDir = Path.Combine(
            Environment.CurrentDirectory,
            "poc",
            "artifacts");
        Directory.CreateDirectory(artifactDir);
        File.AppendAllText(
            Path.Combine(artifactDir, "mcp-auth-diagnostics.log"),
            $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
    }

    public static void AppendDiagnosticPublic(string message)
    {
        AppendDiagnostic(message);
    }
}

public sealed class AuthWindow : Window
{
    private readonly PasswordBox _passwordBox;
    private readonly string _expectedPassword;

    public AuthWindow(string expectedPassword)
    {
        _expectedPassword = expectedPassword;
        Title = "CipherVault POC Authorization";
        Width = 460;
        Height = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _passwordBox = new PasswordBox
        {
            MinHeight = 28,
            Margin = new Thickness(0, 8, 0, 12)
        };

        var approveButton = new Button
        {
            Content = "Approve",
            MinWidth = 90,
            Margin = new Thickness(0, 0, 8, 0)
        };
        approveButton.Click += (_, _) => Approve();

        var denyButton = new Button
        {
            Content = "Deny",
            MinWidth = 90
        };
        denyButton.Click += (_, _) => Deny("denied");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(approveButton);
        buttons.Children.Add(denyButton);

        var panel = new StackPanel
        {
            Margin = new Thickness(20)
        };
        panel.Children.Add(new TextBlock
        {
            Text = "MCP process requests access to the CipherVault POC.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Enter the POC password:",
            Margin = new Thickness(0, 12, 0, 0)
        });
        panel.Children.Add(_passwordBox);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            AuthTools.AppendDiagnosticPublic("auth window loaded");
            _passwordBox.Focus();
            ConfigureAutomation();
        };
    }

    public AuthResult Result { get; private set; } = new(false, "not-completed");

    private void ConfigureAutomation()
    {
        if (Environment.GetEnvironmentVariable("PV_POC_AUTOFILL") != "1")
        {
            AuthTools.AppendDiagnosticPublic("autofill disabled");
            return;
        }

        var delayMilliseconds = int.TryParse(
            Environment.GetEnvironmentVariable("PV_POC_AUTOFILL_DELAY_MS"),
            out var parsedDelay)
            ? parsedDelay
            : 500;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delayMilliseconds)
        };
        AuthTools.AppendDiagnosticPublic($"autofill timer starts in {delayMilliseconds} ms");
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            AuthTools.AppendDiagnosticPublic("autofill timer fired");
            _passwordBox.Password = _expectedPassword;
            Approve();
        };
        timer.Start();
    }

    private void Approve()
    {
        if (_passwordBox.Password == _expectedPassword)
        {
            Result = new AuthResult(true, null);
        }
        else
        {
            Result = new AuthResult(false, "wrong-password");
        }
        Close();
    }

    private void Deny(string reason)
    {
        Result = new AuthResult(false, reason);
        Close();
    }
}

public sealed record AuthResult(bool Approved, string? Reason);
