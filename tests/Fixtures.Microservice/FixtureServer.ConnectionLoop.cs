using System.IO;
using System.Net.Sockets;

namespace Nekolla.Nekostick.Tests.Fixtures.Microservice;

internal sealed partial class FixtureServer
{
    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            await using NetworkStream stream = client.GetStream();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    RequestReadResult? result = await HttpRequest.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (result is null)
                    {
                        return;
                    }

                    if (result.ErrorStatusCode is not null)
                    {
                        await SendFixedResponseAsync(
                            stream,
                            result.ErrorStatusCode.Value,
                            "fixture request error\n",
                            closeConnection: true,
                            isHead: false,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    HttpRequest request = result.Request!;
                    if (request.Path == "/fixture/health")
                    {
                        await SendHealthAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    }
                    else if (_options.Mode == FixtureOptions.FixtureMode.Hold)
                    {
                        await HoldAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        if (_options.DelayMilliseconds > 0)
                        {
                            await Task.Delay(_options.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
                        }

                        if (ShouldUpgrade(request))
                        {
                            await HandleWebSocketAsync(stream, request, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        await SendModeResponseAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    }

                    if (request.ConnectionClose)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The parent process is shutting down.
            }
            catch (IOException)
            {
                // The test client may close a deliberately slow connection.
            }
            catch (SocketException)
            {
                // The test client may reset a deliberately slow connection.
            }
            catch (Exception)
            {
                // Request data and exception details must never be emitted by this fixture.
            }
        }
    }

    private bool ShouldUpgrade(HttpRequest request)
    {
        var upgradeRequested = request.Headers.HasToken("connection", "upgrade")
            && string.Equals(request.Headers.Get("upgrade"), "websocket", StringComparison.OrdinalIgnoreCase);
        return upgradeRequested
            && (_options.Mode == FixtureOptions.FixtureMode.WebSocket || request.Path == "/fixture/ws");
    }

    private async Task HoldAsync(CancellationToken cancellationToken)
    {
        if (_options.HoldMilliseconds == 0)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Task.Delay(_options.HoldMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }
}
