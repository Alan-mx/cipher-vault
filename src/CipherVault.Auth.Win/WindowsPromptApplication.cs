using System.Windows;

namespace CipherVault.Auth.Win;

internal static class WindowsPromptApplication
{
    private static readonly object Gate = new();
    private static Application? _application;

    public static Application Get()
    {
        lock (Gate)
        {
            if (_application is not null)
            {
                return _application;
            }

            var ready = new ManualResetEventSlim(false);
            Exception? startupException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    _application = new Application
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown
                    };
                    ready.Set();
                    _application.Run();
                }
                catch (Exception exception)
                {
                    startupException = exception;
                    ready.Set();
                }
            })
            {
                IsBackground = true,
                Name = "CipherVault.AuthDispatcher"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            if (startupException is not null || _application is null)
            {
                throw new InvalidOperationException(
                    "无法启动 Windows 授权窗口。",
                    startupException);
            }

            return _application;
        }
    }
}
