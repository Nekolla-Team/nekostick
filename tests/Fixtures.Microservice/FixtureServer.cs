using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Nekolla.Nekostick.Tests.Fixtures.Microservice;

internal sealed partial class FixtureServer : IDisposable
{
    private readonly FixtureOptions _options;
    private readonly TcpListener _listener;
    private readonly ConcurrentBag<Task> _connections = new();
    private int _port;

    internal FixtureServer(FixtureOptions options)
    {
        _options = options;
        _listener = new TcpListener(options.ListenIpAddress, options.Port);
    }

    internal int Port => _port;

    internal void Start()
    {
        _listener.Start(128);
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                client.NoDelay = true;
                _connections.Add(HandleConnectionAsync(client, cancellationToken));
            }
        }
        finally
        {
            _listener.Stop();
            try
            {
                await Task.WhenAll(_connections).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Individual connections are cancelled as part of graceful shutdown.
            }
        }
    }

    public void Dispose() => _listener.Stop();
}
