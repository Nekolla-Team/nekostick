using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Reflection;

namespace Nekolla.Nekostick.IntegrationTests;

internal sealed class InProcessFixtureServer : IAsyncDisposable
{
    private readonly object _server;
    private readonly CancellationTokenSource _shutdown;
    private readonly Task _runTask;

    private InProcessFixtureServer(object server, int port, CancellationTokenSource shutdown, Task runTask)
    {
        _server = server;
        Port = port;
        _shutdown = shutdown;
        _runTask = runTask;
    }

    internal int Port { get; }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Readiness is a safe stage probe and must never expose transport details.")]
    internal async ValueTask<IntegrationStageEvidence> WaitUntilReadyAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}/")
        };

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync(
                    "/fixture/health",
                    timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return new(IntegrationStageKind.FixtureReady);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException)
            {
            }
            catch (Exception)
            {
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        return new(IntegrationStageKind.FixtureNotReady);
    }

    internal static InProcessFixtureServer Start(string mode, params string[] additionalArguments)
    {
        var assembly = Assembly.Load("Fixtures.Microservice");
        var optionsType = assembly.GetType(
            "Nekolla.Nekostick.Tests.Fixtures.Microservice.FixtureOptions",
            throwOnError: true)!;
        var serverType = assembly.GetType(
            "Nekolla.Nekostick.Tests.Fixtures.Microservice.FixtureServer",
            throwOnError: true)!;
        var arguments = new List<string> { "--mode", mode, "--port", "0" };
        arguments.AddRange(additionalArguments);
        var parse = optionsType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parseArguments = new object?[] { arguments.ToArray(), null };
        var options = parse.Invoke(null, parseArguments)!;
        var server = Activator.CreateInstance(
            serverType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [options],
            culture: null)!;
        serverType.GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, null);
        var port = (int)serverType.GetProperty(
            "Port",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(server)!;
        var shutdown = new CancellationTokenSource();
        var runTask = (Task)serverType.GetMethod(
            "RunAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(server, [shutdown.Token])!;
        return new InProcessFixtureServer(server, port, shutdown, runTask);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        finally
        {
            ((IDisposable)_server).Dispose();
            _shutdown.Dispose();
        }
    }
}
