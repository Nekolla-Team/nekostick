using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Nekolla.Nekostick.Tests.Fixtures.Microservice;

internal static class Program
{
    private const int StartupFailureExitCode = 3;

    internal static async Task<int> Main(string[] args)
    {
        FixtureOptions? options;
        try
        {
            options = FixtureOptions.Parse(args, out var showHelp);
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
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    @event = "ready",
                    address = options.ListenAddress,
                    port = server.Port,
                    protocol = "http/1.1",
                }));
                await Console.Out.FlushAsync().ConfigureAwait(false);

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
