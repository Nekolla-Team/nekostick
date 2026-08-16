using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Nekolla.Nekostick.Tests.Fixtures.Microservice;

internal sealed partial class FixtureServer
{
    private static async Task SendHealthAsync(NetworkStream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        const string body = "{\"status\":\"ok\",\"ready\":true,\"protocol\":\"http/1.1\"}";
        await SendBodyAsync(
            stream,
            200,
            "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(body),
            request.IsHead,
            request.ConnectionClose,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendModeResponseAsync(NetworkStream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        switch (_options.Mode)
        {
            case FixtureOptions.FixtureMode.Stream:
                await SendGeneratedResponseAsync(stream, request, cancellationToken).ConfigureAwait(false);
                break;
            case FixtureOptions.FixtureMode.Fail:
                await SendFixedResponseAsync(
                    stream,
                    _options.FailureStatusCode,
                    "fixture failure\n",
                    request.ConnectionClose,
                    request.IsHead,
                    cancellationToken).ConfigureAwait(false);
                break;
            case FixtureOptions.FixtureMode.WebSocket:
                await SendFixedResponseAsync(
                    stream,
                    426,
                    "websocket upgrade required\n",
                    request.ConnectionClose,
                    request.IsHead,
                    cancellationToken).ConfigureAwait(false);
                break;
            case FixtureOptions.FixtureMode.Echo:
            case FixtureOptions.FixtureMode.Delay:
            case FixtureOptions.FixtureMode.Mixed:
                await SendEchoAsync(stream, request, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await SendFixedResponseAsync(stream, 500, "fixture failure\n", true, false, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static async Task SendEchoAsync(NetworkStream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            method = request.Method,
            path = request.Path,
            query = new
            {
                present = request.Query.Present,
                parameterCount = request.Query.ParameterCount,
                hasEmptyParameter = request.Query.HasEmptyParameter,
                hasKeylessParameter = request.Query.HasKeylessParameter,
                hasEmptyValue = request.Query.HasEmptyValue,
                hasPercentEncoding = request.Query.HasPercentEncoding,
            },
            headerPresence = new
            {
                authorization = request.Headers.Contains("authorization"),
                cookie = request.Headers.Contains("cookie"),
                host = request.Headers.Contains("host"),
                contentType = request.Headers.Contains("content-type"),
                contentLength = request.Headers.Contains("content-length"),
                transferEncoding = request.Headers.Contains("transfer-encoding"),
                connection = request.Headers.Contains("connection"),
                upgrade = request.Headers.Contains("upgrade"),
                secWebSocketKey = request.Headers.Contains("sec-websocket-key"),
                xForwardedFor = request.Headers.Contains("x-forwarded-for"),
                xForwardedHost = request.Headers.Contains("x-forwarded-host"),
                xForwardedProto = request.Headers.Contains("x-forwarded-proto"),
                xRealIp = request.Headers.Contains("x-real-ip"),
            },
        });

        await SendBodyAsync(
            stream,
            200,
            "application/json; charset=utf-8",
            body,
            request.IsHead,
            request.ConnectionClose,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendGeneratedResponseAsync(NetworkStream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        var chunked = _options.Chunked;
        var closeConnection = request.ConnectionClose;
        await SendHeadersAsync(
            stream,
            200,
            "application/octet-stream",
            chunked ? null : _options.ResponseBytes,
            closeConnection,
            chunked,
            cancellationToken).ConfigureAwait(false);

        if (request.IsHead)
        {
            return;
        }

        var remaining = _options.ResponseBytes;
        var offset = 0L;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, _options.ChunkSize);
            var buffer = new byte[count];
            FillPattern(buffer, offset, _options.ResponsePattern);

            if (chunked)
            {
                await WriteAsciiAsync(stream, count.ToString("X", CultureInfo.InvariantCulture) + "\r\n", cancellationToken)
                    .ConfigureAwait(false);
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                await WriteAsciiAsync(stream, "\r\n", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            remaining -= count;
            offset += count;
            if (remaining > 0 && _options.ChunkDelayMilliseconds > 0)
            {
                await Task.Delay(_options.ChunkDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        if (chunked)
        {
            await WriteAsciiAsync(stream, "0\r\n\r\n", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendFixedResponseAsync(
        NetworkStream stream,
        int statusCode,
        string body,
        bool closeConnection,
        bool isHead,
        CancellationToken cancellationToken)
    {
        await SendBodyAsync(
            stream,
            statusCode,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(body),
            isHead,
            closeConnection,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendBodyAsync(
        NetworkStream stream,
        int statusCode,
        string contentType,
        byte[] body,
        bool isHead,
        bool closeConnection,
        CancellationToken cancellationToken)
    {
        await SendHeadersAsync(
            stream,
            statusCode,
            contentType,
            body.Length,
            closeConnection,
            chunked: false,
            cancellationToken).ConfigureAwait(false);

        if (!isHead)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendHeadersAsync(
        NetworkStream stream,
        int statusCode,
        string contentType,
        int? contentLength,
        bool closeConnection,
        bool chunked,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(256);
        builder.Append("HTTP/1.1 ")
            .Append(statusCode.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(GetReasonPhrase(statusCode))
            .Append("\r\nContent-Type: ")
            .Append(contentType)
            .Append("\r\n");

        if (chunked)
        {
            builder.Append("Transfer-Encoding: chunked\r\n");
        }
        else
        {
            builder.Append("Content-Length: ")
                .Append(contentLength!.Value.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n");
        }

        builder.Append("Connection: ")
            .Append(closeConnection ? "close" : "keep-alive")
            .Append("\r\n\r\n");
        await WriteAsciiAsync(stream, builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private static void FillPattern(byte[] destination, long offset, byte[] pattern)
    {
        var patternOffset = (int)(offset % pattern.Length);
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = pattern[patternOffset];
            patternOffset++;
            if (patternOffset == pattern.Length)
            {
                patternOffset = 0;
            }
        }
    }

    private static async Task WriteAsciiAsync(NetworkStream stream, string value, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(value), cancellationToken).ConfigureAwait(false);
    }

    private static string GetReasonPhrase(int statusCode)
    {
        return statusCode switch
        {
            101 => "Switching Protocols",
            200 => "OK",
            400 => "Bad Request",
            413 => "Payload Too Large",
            417 => "Expectation Failed",
            426 => "Upgrade Required",
            431 => "Request Header Fields Too Large",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            505 => "HTTP Version Not Supported",
            _ when statusCode is >= 400 and <= 599 => "Fixture Failure",
            _ => "Fixture Response",
        };
    }
}
