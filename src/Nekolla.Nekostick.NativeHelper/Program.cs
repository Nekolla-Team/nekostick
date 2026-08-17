using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Nekolla.Nekostick.NativeHelper;

internal static partial class Program
{
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private const int ExitUnavailable = 78;
    private const int ExitInvalid = 64;
    private const string ReadyMarker = "NK_READY";
    private const string FailedMarker = "NK_FAILED";
    private const string UnavailableMarker = "NK_UNAVAILABLE";

    public static async Task<int> Main(string[] args)
    {
        if (!TryReadGracePeriod(args, out var gracePeriod) || !NativeMethods.IsSupported)
        {
            WriteMarker(UnavailableMarker);
            return ExitUnavailable;
        }

        LaunchRequest? request;
        try
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            request = string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<LaunchRequest>(line);
        }
        catch
        {
            request = null;
        }

        if (request is null || !request.IsValid())
        {
            WriteMarker(FailedMarker);
            return ExitInvalid;
        }

        var processGroupId = NativeMethods.CreateOwnProcessGroup();
        if (processGroupId <= 1 || processGroupId != Environment.ProcessId)
        {
            WriteMarker(UnavailableMarker);
            return ExitUnavailable;
        }

        using var shutdown = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });

        using var child = CreateChild(request);
        try
        {
            if (!child.Start())
            {
                WriteMarker(FailedMarker);
                return ExitInvalid;
            }
        }
        catch
        {
            WriteMarker(FailedMarker);
            return ExitInvalid;
        }

        WriteMarker(ReadyMarker);
        var stdout = RelayAsync(child.StandardOutput.BaseStream, Console.OpenStandardOutput());
        var stderr = RelayAsync(child.StandardError.BaseStream, Console.OpenStandardError());
        var wait = child.WaitForExitAsync();
        var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
        var completed = await Task.WhenAny(wait, cancellation).ConfigureAwait(false);
        if (completed == wait)
        {
            try
            {
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // Descendants holding inherited pipes are cleaned by the group signal.
            }
        }

        await StopGroupAsync(
            child,
            processGroupId,
            completed == wait ? TimeSpan.FromMilliseconds(25) : gracePeriod).ConfigureAwait(false);

        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch
        {
            // The fixed helper boundary intentionally does not expose stream errors.
        }

        return child.HasExited ? child.ExitCode : 0;
    }

    private static Process CreateChild(LaunchRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false, false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false, false)
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo, EnableRaisingEvents = false };
    }

    private static async Task RelayAsync(Stream source, Stream destination)
    {
        try
        {
            await source.CopyToAsync(destination).ConfigureAwait(false);
            await destination.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task StopGroupAsync(Process child, int processGroupId, TimeSpan gracePeriod)
    {
        NativeMethods.TrySignalGroup(processGroupId, SigTerm);
        if (!child.HasExited)
        {
            var deadline = DateTimeOffset.UtcNow.Add(gracePeriod);
            while (!child.HasExited && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }
        }
        else
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
        }

        NativeMethods.TrySignalGroup(processGroupId, SigKill);
        try
        {
            await child.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // The helper exits through the process-group signal when forced.
        }
    }

    private static bool TryReadGracePeriod(string[] args, out TimeSpan gracePeriod)
    {
        gracePeriod = TimeSpan.FromSeconds(15);
        if (args.Length == 0)
        {
            return true;
        }

        if (args.Length != 2 || !string.Equals(args[0], "--grace-ms", StringComparison.Ordinal) ||
            !int.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds) ||
            milliseconds is < 1 or > 300_000)
        {
            return false;
        }

        gracePeriod = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private static void WriteMarker(string marker)
    {
        try
        {
            Console.Error.WriteLine(marker);
            Console.Error.Flush();
        }
        catch
        {
            // There is no safe public diagnostic channel at this boundary.
        }
    }

    private sealed record LaunchRequest(string FileName, string WorkingDirectory, string[] Arguments)
    {
        internal bool IsValid() =>
            FileName is not null &&
            WorkingDirectory is not null &&
            Arguments is not null &&
            FileName.StartsWith('/') &&
            WorkingDirectory.StartsWith('/') &&
            !FileName.Contains('\\') &&
            !WorkingDirectory.Contains('\\') &&
            !FileName.Any(char.IsControl) &&
            !WorkingDirectory.Any(char.IsControl) &&
            Arguments.Length <= 256 &&
            Arguments.All(argument => argument is not null && argument.Length <= 32 * 1024 && !argument.Any(char.IsControl));
    }

    private static partial class NativeMethods
    {
        internal static bool IsSupported =>
            (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
            (RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64);

        internal static int CreateOwnProcessGroup()
        {
            try
            {
                var result = Setsid();
                return result < 0 ? -1 : GetProcessGroup(0);
            }
            catch
            {
                return -1;
            }
        }

        internal static bool TrySignalGroup(int processGroupId, int signal)
        {
            if (processGroupId <= 1)
            {
                return false;
            }

            try
            {
                var ownGroup = GetProcessGroup(Environment.ProcessId);
                if (ownGroup != processGroupId || processGroupId != Environment.ProcessId)
                {
                    return false;
                }

                return Kill(-processGroupId, signal) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static int GetProcessGroup(int processId) => OperatingSystem.IsMacOS() ? GetProcessGroupDarwin(processId) : GetProcessGroupLinux(processId);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "setsid")]
        private static partial int SetsidDarwin();
        [LibraryImport("libc.so.6", EntryPoint = "setsid")]
        private static partial int SetsidLinux();
        [LibraryImport("libSystem.B.dylib", EntryPoint = "getpgid")]
        private static partial int GetProcessGroupDarwin(int processId);
        [LibraryImport("libc.so.6", EntryPoint = "getpgid")]
        private static partial int GetProcessGroupLinux(int processId);
        [LibraryImport("libSystem.B.dylib", EntryPoint = "kill")]
        private static partial int KillDarwin(int processId, int signal);
        [LibraryImport("libc.so.6", EntryPoint = "kill")]
        private static partial int KillLinux(int processId, int signal);
        private static int Setsid() => OperatingSystem.IsMacOS() ? SetsidDarwin() : SetsidLinux();
        private static int Kill(int processId, int signal) => OperatingSystem.IsMacOS() ? KillDarwin(processId, signal) : KillLinux(processId, signal);
    }
}
