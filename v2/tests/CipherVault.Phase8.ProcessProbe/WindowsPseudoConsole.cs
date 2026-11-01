using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CipherVault.Phase8.ProcessProbe;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPseudoConsole : ServerProcess
{
    private readonly IntPtr processHandle;
    private readonly IntPtr pseudoConsole;
    private readonly FileStream output;
    private readonly FileStream input;
    private readonly StringBuilder recentOutput = new();
    private readonly object outputLock = new();
    private Task? outputPump;
    private int exitCode = -1;

    private WindowsPseudoConsole(
        IntPtr processHandle,
        IntPtr pseudoConsole,
        FileStream output,
        FileStream input,
        int processId)
    {
        this.processHandle = processHandle;
        this.pseudoConsole = pseudoConsole;
        this.output = output;
        this.input = input;
        ProcessId = processId;
    }

    public override int ProcessId { get; }

    public static WindowsPseudoConsole Start(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        if (!NativeMethods.HasConsole())
        {
            _ = NativeMethods.FreeConsole();
            if (NativeMethods.AllocConsole())
            {
                var window = NativeMethods.GetConsoleWindow();
                if (window != IntPtr.Zero)
                {
                    _ = NativeMethods.ShowWindow(window, 0);
                }
            }
        }
        if (!NativeMethods.CreatePipe(
                out var inputRead,
                out var inputWrite,
                IntPtr.Zero,
                0)
            || !NativeMethods.CreatePipe(
                out var outputRead,
                out var outputWrite,
                IntPtr.Zero,
                0))
        {
            throw new ProbeException("pseudo_console_pipe_failed");
        }

        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        try
        {
            if (NativeMethods.CreatePseudoConsole(
                    new NativeMethods.Coord(160, 40),
                    inputRead,
                    outputWrite,
                    0,
                    out pseudoConsole) != 0)
            {
                throw new ProbeException("pseudo_console_create_failed");
            }
            var attributeBytes = IntPtr.Zero;
            _ = NativeMethods.InitializeProcThreadAttributeList(
                IntPtr.Zero,
                1,
                0,
                ref attributeBytes);
            attributeList = Marshal.AllocHGlobal(attributeBytes);
            if (!NativeMethods.InitializeProcThreadAttributeList(
                    attributeList,
                    1,
                    0,
                    ref attributeBytes)
                || !NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    NativeMethods.ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new ProbeException(
                    "pseudo_console_attributes_failed");
            }

            var startup = new NativeMethods.StartupInfoEx
            {
                StartupInfo = new NativeMethods.StartupInfo
                {
                    Size = Marshal.SizeOf<NativeMethods.StartupInfoEx>()
                },
                AttributeList = attributeList
            };
            var commandLine = new StringBuilder(
                Quote(executable)
                + (arguments.Count == 0
                    ? string.Empty
                    : " " + string.Join(" ", arguments.Select(Quote))));
            var workingDirectory = Path.GetDirectoryName(executable)
                ?? Environment.CurrentDirectory;
            foreach (var pair in environment)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
            if (!NativeMethods.CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startup,
                    out var processInformation))
            {
                throw new ProbeException("pseudo_console_process_failed");
            }
            NativeMethods.CloseHandle(inputRead);
            inputRead = IntPtr.Zero;
            NativeMethods.CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;
            NativeMethods.CloseHandle(processInformation.Thread);
            var session = new WindowsPseudoConsole(
                processInformation.Process,
                pseudoConsole,
                new FileStream(
                    new SafeFileHandle(outputRead, ownsHandle: true),
                    FileAccess.Read,
                    bufferSize: 4096,
                    isAsync: false),
                new FileStream(
                    new SafeFileHandle(inputWrite, ownsHandle: true),
                    FileAccess.Write,
                    bufferSize: 4096,
                    isAsync: false),
                checked((int)processInformation.ProcessId));
            session.EnsureOutputPump();
            return session;
        }
        catch
        {
            if (inputRead != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(inputRead);
            }
            if (inputWrite != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(inputWrite);
            }
            if (outputRead != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(outputRead);
            }
            if (outputWrite != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(outputWrite);
            }
            if (pseudoConsole != IntPtr.Zero)
            {
                NativeMethods.ClosePseudoConsole(pseudoConsole);
            }
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    public override async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        if (exitCode >= 0)
        {
            return exitCode;
        }
        var wait = await Task.Run(() =>
            NativeMethods.WaitForSingleObject(
                processHandle,
                checked((uint)timeout.TotalMilliseconds)));
        if (wait == NativeMethods.WaitTimeout)
        {
            await TerminateAsync();
            throw new ProbeException("server_shutdown_timeout");
        }
        if (wait != NativeMethods.WaitObject0
            || !NativeMethods.GetExitCodeProcess(
                processHandle,
                out var code))
        {
            throw new ProbeException("server_wait_failed");
        }
        exitCode = unchecked((int)code);
        return exitCode;
    }

    public override Task RequestGracefulShutdownAsync() =>
        WriteAsync("\u0003");

    public override Task TerminateAsync()
    {
        if (processHandle != IntPtr.Zero)
        {
            _ = NativeMethods.TerminateProcess(processHandle, 1);
        }
        return Task.CompletedTask;
    }

    public async Task WaitForOutputAsync(
        string marker,
        TimeSpan timeout)
    {
        EnsureOutputPump();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (outputLock)
            {
                if (recentOutput.ToString().Contains(
                        marker,
                        StringComparison.Ordinal))
                {
                    return;
                }
            }
            await Task.Delay(100);
        }
        throw new ProbeException("interactive_console_prompt_timeout");
    }

    public Task WriteSecretAsync(string secret) =>
        WriteAsync(secret + "\r", clearBuffer: true);

    public override async ValueTask DisposeAsync()
    {
        if (processHandle != IntPtr.Zero)
        {
            _ = NativeMethods.TerminateProcess(processHandle, 1);
            NativeMethods.CloseHandle(processHandle);
        }
        await input.DisposeAsync();
        await output.DisposeAsync();
        if (outputPump is not null)
        {
            try
            {
                await outputPump;
            }
            catch
            {
                // The process and pseudo-console are being torn down.
            }
        }
        if (pseudoConsole != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(pseudoConsole);
        }
    }

    private async Task WriteAsync(
        string value,
        bool clearBuffer = false)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            await input.WriteAsync(bytes);
            await input.FlushAsync();
        }
        finally
        {
            if (clearBuffer)
            {
                Array.Clear(bytes);
            }
        }
    }

    private void EnsureOutputPump()
    {
        outputPump ??= PumpOutputAsync();
    }

    private async Task PumpOutputAsync()
    {
        var bytes = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[4096];
        while (true)
        {
            var read = await output.ReadAsync(bytes);
            if (read == 0)
            {
                return;
            }
            var count = decoder.GetChars(
                bytes,
                0,
                read,
                characters,
                0);
            lock (outputLock)
            {
                recentOutput.Append(characters, 0, count);
                if (recentOutput.Length > 8192)
                {
                    recentOutput.Remove(0, recentOutput.Length - 4096);
                }
            }
        }
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
