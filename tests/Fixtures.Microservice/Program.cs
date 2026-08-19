using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Nekolla.Nekostick.Tests.Fixtures.Microservice;

internal static class Program
{
    private const int StartupFailureExitCode = 3;
    private const int DescendantUnavailableExitCode = 78;
    private const string DescendantChildArgument = "--fixture-descendant-child";
    private const string DescendantModeName = "descendant";

    internal static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], DescendantChildArgument, StringComparison.Ordinal))
        {
            return await RunDescendantChildAsync().ConfigureAwait(false);
        }

        var descendantMode = TryRewriteDescendantMode(args, out var fixtureArguments);
        if (descendantMode && !IsSupportedPosix())
        {
            WriteError("descendant mode is unavailable");
            return DescendantUnavailableExitCode;
        }

        FixtureOptions? options;
        try
        {
            options = FixtureOptions.Parse(fixtureArguments, out var showHelp);

            if (showHelp)
            {
                Console.WriteLine(FixtureOptions.Usage);
                return 0;
            }
        }
        catch (FixtureArgumentException)
        {
            WriteError("invalid command line");
            return 2;
        }

        if (options is null)
        {
            WriteError("invalid command line");
            return 2;
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

        ConsoleCancelEventHandler? cancelHandler = null;
        if (OperatingSystem.IsWindows())
        {
            cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
        }

        try
        {
            if (options.StartupDelayMilliseconds > 0)
            {
                await Task.Delay(options.StartupDelayMilliseconds, shutdown.Token).ConfigureAwait(false);
            }

            if (options.FailStartup)
            {
                WriteError("deliberate startup failure");
                return StartupFailureExitCode;
            }

            FixtureServer server;
            try
            {
                server = new FixtureServer(options);
                server.Start();
            }
            catch (Exception) when (shutdown.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception)
            {
                WriteError("unable to start fixture");
                return StartupFailureExitCode;
            }

            using (server)
            {
                DescendantHandle? descendant = null;
                try
                {
                    if (descendantMode)
                    {
                        descendant = await StartDescendantAsync().ConfigureAwait(false);
                    }

                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        @event = "ready",
                        address = options.ListenAddress,
                        port = server.Port,
                        protocol = "http/1.1",
                    }));
                    await Console.Out.FlushAsync().ConfigureAwait(false);

                    if (descendant is not null)
                    {
                        var leaderProcessGroupId = GetProcessGroup(Environment.ProcessId);
                        var descendantAlive = !descendant.HasExited;
                        if (leaderProcessGroupId <= 1 ||
                            leaderProcessGroupId != descendant.ProcessGroupId ||
                            !descendantAlive)
                        {
                            throw new InvalidOperationException("descendant process-group readiness failed");
                        }

                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            @event = "descendant-ready",
                            leaderProcessId = Environment.ProcessId,
                            leaderProcessGroupId,
                            descendantProcessId = descendant.ProcessId,
                            descendantProcessGroupId = descendant.ProcessGroupId,
                            leaderAlive = true,
                            descendantAlive,
                        }));
                        await Console.Out.FlushAsync().ConfigureAwait(false);
                    }

                    Task exitTask = ExitAfterAsync(options.ExitAfterMilliseconds, shutdown);
                    try
                    {
                        await server.RunAsync(shutdown.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        shutdown.Cancel();
                        await exitTask.ConfigureAwait(false);
                    }
                }
                finally
                {
                    descendant?.Dispose();
                }
            }


            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception)
        {
            WriteError("fixture failure");
            return 1;
        }
        finally
        {
            if (cancelHandler is not null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
    }

    private static async Task<int> RunDescendantChildAsync()
    {
        if (!IsSupportedPosix())
        {
            return DescendantUnavailableExitCode;
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

        var processGroupId = GetProcessGroup(Environment.ProcessId);
        if (processGroupId <= 1)
        {
            return DescendantUnavailableExitCode;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            @event = "descendant-ready",
            processId = Environment.ProcessId,
            processGroupId,
        }));
        await Console.Out.FlushAsync().ConfigureAwait(false);

        try
        {
            await Console.In.ReadLineAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }

        return 0;

    }

    private static bool TryRewriteDescendantMode(string[] args, out string[] fixtureArguments)
    {
        fixtureArguments = args;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--mode", StringComparison.Ordinal) &&
                index + 1 < args.Length &&
                string.Equals(args[index + 1], DescendantModeName, StringComparison.Ordinal))
            {
                fixtureArguments = args.ToArray();
                fixtureArguments[index + 1] = "echo";
                return true;
            }

            if (args[index].StartsWith("--mode=", StringComparison.Ordinal) &&
                string.Equals(args[index][7..], DescendantModeName, StringComparison.Ordinal))
            {
                fixtureArguments = args.ToArray();
                fixtureArguments[index] = "--mode=echo";
                return true;
            }
        }

        return false;
    }

    private static async Task<DescendantHandle> StartDescendantAsync()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("fixture process path is unavailable");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(DescendantChildArgument);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = false };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("descendant process did not start");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var marker = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (!TryReadDescendantReady(marker, process.Id, out var processGroupId) ||
                GetProcessGroup(process.Id) != processGroupId)
            {
                throw new InvalidOperationException("descendant process did not become ready");
            }

            return new DescendantHandle(process, processGroupId);
        }
        catch
        {
            try
            {
                process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }

            process.Dispose();
            throw;
        }

    }

    private static bool TryReadDescendantReady(
        string? marker,
        int expectedProcessId,
        out int processGroupId)
    {
        processGroupId = 0;
        if (marker is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(marker);
            var root = document.RootElement;
            return root.TryGetProperty("event", out var eventProperty) &&
                eventProperty.ValueKind == JsonValueKind.String &&
                string.Equals(eventProperty.GetString(), "descendant-ready", StringComparison.Ordinal) &&
                root.TryGetProperty("processId", out var processProperty) &&
                processProperty.TryGetInt32(out var processId) &&
                processId == expectedProcessId &&
                root.TryGetProperty("processGroupId", out var groupProperty) &&
                groupProperty.TryGetInt32(out processGroupId) &&
                processGroupId > 1;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsSupportedPosix() =>
        (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
        (RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64) &&
        (RuntimeInformation.RuntimeIdentifier is "osx-arm64" or "osx-x64" or "linux-arm64" or "linux-x64");

    private static int GetProcessGroup(int processId)
    {
        try
        {
            return OperatingSystem.IsMacOS()
                ? GetProcessGroupDarwin(processId)
                : GetProcessGroupLinux(processId);
        }
        catch
        {
            return -1;
        }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "getpgid", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetProcessGroupDarwin(int processId);

    [DllImport("libc.so.6", EntryPoint = "getpgid", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetProcessGroupLinux(int processId);

    private sealed class DescendantHandle : IDisposable
    {
        private readonly Process _process;

        internal DescendantHandle(Process process, int processGroupId)
        {
            _process = process;
            ProcessGroupId = processGroupId;
        }

        internal int ProcessId => _process.Id;
        internal int ProcessGroupId { get; }
        internal bool HasExited => _process.HasExited;

        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch
            {
            }

            _process.Dispose();
        }
    }

    private static async Task ExitAfterAsync(int milliseconds, CancellationTokenSource shutdown)
    {
        if (milliseconds == 0)
        {
            return;
        }

        try
        {
            await Task.Delay(milliseconds, shutdown.Token).ConfigureAwait(false);
            shutdown.Cancel();
        }
        catch (OperationCanceledException)
        {
            // Shutdown is already in progress.
        }
    }

    private static void WriteError(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.Flush();
    }
}
