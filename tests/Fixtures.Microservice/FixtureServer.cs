using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nekolla.Nekostick.Tests.Fixtures.Microservice;

internal sealed class FixtureServer : IDisposable
{
    private const int MaximumHeaderBytes = 64 * 1_024;
    private const int MaximumHeaderLineBytes = 8 * 1_024;
    private const int MaximumHeaderCount = 100;
    private const int MaximumWebSocketMessageBytes = 1 * 1_024 * 1_024;
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

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

    private async Task HandleWebSocketAsync(NetworkStream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Method, "GET", StringComparison.Ordinal)
            || !request.Headers.HasToken("connection", "upgrade")
            || !string.Equals(request.Headers.Get("upgrade"), "websocket", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(request.Headers.Get("sec-websocket-version"), "13", StringComparison.Ordinal)
            || !TryGetWebSocketAccept(request.Headers.Get("sec-websocket-key"), out var accept))
        {
            await SendFixedResponseAsync(stream, 400, "invalid websocket upgrade\n", true, request.IsHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteAsciiAsync(
            stream,
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + "Sec-WebSocket-Accept: "
            + accept
            + "\r\n\r\n",
            cancellationToken).ConfigureAwait(false);

        using var closeTimerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? closeTimer = _options.WebSocketCloseAfterMilliseconds > 0
            ? Task.Delay(_options.WebSocketCloseAfterMilliseconds, closeTimerCancellation.Token)
            : null;
        var frameCount = 0;
        var fragmentedMessage = new MemoryStream();
        var fragmentedOpcode = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task<WsFrame?> frameTask = ReadWebSocketFrameAsync(stream, closeTimerCancellation.Token);
                if (closeTimer is not null)
                {
                    var completed = await Task.WhenAny(frameTask, closeTimer).ConfigureAwait(false);
                    if (completed == closeTimer)
                    {
                        closeTimerCancellation.Cancel();
                        await IgnoreCancellationAsync(frameTask).ConfigureAwait(false);
                        await SendWebSocketCloseAsync(stream, _options.WebSocketCloseCode, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                WsFrame? frame = await frameTask.ConfigureAwait(false);
                if (frame is null)
                {
                    return;
                }

                if (frame.Opcode is 8 or 9 or 10)
                {
                    if (!frame.Fin || frame.Payload.Length > 125)
                    {
                        await SendWebSocketCloseAsync(stream, 1002, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (frame.Opcode == 8)
                    {
                        await SendWebSocketCloseAsync(stream, 1000, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (frame.Opcode == 9)
                    {
                        await SendWebSocketFrameAsync(stream, 10, frame.Payload, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (frame.Opcode is 1 or 2)
                {
                    if (fragmentedOpcode != 0)
                    {
                        await SendWebSocketCloseAsync(stream, 1002, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    fragmentedOpcode = frame.Opcode;
                }
                else if (frame.Opcode == 0)
                {
                    if (fragmentedOpcode == 0)
                    {
                        await SendWebSocketCloseAsync(stream, 1002, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    await SendWebSocketCloseAsync(stream, 1002, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (fragmentedMessage.Length + frame.Payload.Length > MaximumWebSocketMessageBytes)
                {
                    await SendWebSocketCloseAsync(stream, 1009, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await fragmentedMessage.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                if (!frame.Fin)
                {
                    continue;
                }

                frameCount++;
                await SendWebSocketFrameAsync(
                    stream,
                    fragmentedOpcode,
                    fragmentedMessage.ToArray(),
                    cancellationToken).ConfigureAwait(false);
                fragmentedMessage.SetLength(0);
                fragmentedOpcode = 0;

                if (_options.WebSocketCloseAfterFrames > 0 && frameCount >= _options.WebSocketCloseAfterFrames)
                {
                    await SendWebSocketCloseAsync(stream, _options.WebSocketCloseCode, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (WebSocketProtocolException)
        {
            await SendWebSocketCloseAsync(stream, 1002, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            closeTimerCancellation.Cancel();
            fragmentedMessage.Dispose();
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

    private static bool TryGetWebSocketAccept(string? key, out string accept)
    {
        accept = string.Empty;
        if (key is null)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(key);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length != 16)
        {
            return false;
        }

        var input = Encoding.ASCII.GetBytes(key + WebSocketGuid);
#pragma warning disable CA5350 // RFC 6455 requires SHA-1 for the WebSocket accept value.
        accept = Convert.ToBase64String(SHA1.HashData(input));
#pragma warning restore CA5350
        return true;
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task SendWebSocketCloseAsync(NetworkStream stream, ushort code, CancellationToken cancellationToken)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, code);
        await SendWebSocketFrameAsync(stream, 8, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendWebSocketFrameAsync(
        NetworkStream stream,
        int opcode,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaximumWebSocketMessageBytes)
        {
            throw new WebSocketProtocolException();
        }

        var headerLength = payload.Length < 126 ? 2 : payload.Length <= ushort.MaxValue ? 4 : 10;
        var header = new byte[headerLength];
        header[0] = (byte)(0x80 | opcode);
        if (payload.Length < 126)
        {
            header[1] = (byte)payload.Length;
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)payload.Length);
        }
        else
        {
            header[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(2), (ulong)payload.Length);
        }

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WsFrame?> ReadWebSocketFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var firstTwoBytes = new byte[2];
        if (!await ReadExactlyOrEofAsync(stream, firstTwoBytes, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var fin = (firstTwoBytes[0] & 0x80) != 0;
        var reservedBits = firstTwoBytes[0] & 0x70;
        var opcode = firstTwoBytes[0] & 0x0F;
        var masked = (firstTwoBytes[1] & 0x80) != 0;
        var lengthCode = firstTwoBytes[1] & 0x7F;
        if (reservedBits != 0 || !masked)
        {
            throw new WebSocketProtocolException();
        }

        ulong length = lengthCode switch
        {
            < 126 => (ulong)lengthCode,
            126 => await ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false),
            _ => await ReadUInt64Async(stream, cancellationToken).ConfigureAwait(false),
        };

        if (length > MaximumWebSocketMessageBytes || length > int.MaxValue || (lengthCode == 127 && (length & (1UL << 63)) != 0))
        {
            throw new WebSocketProtocolException();
        }

        var mask = new byte[4];
        if (!await ReadExactlyOrEofAsync(stream, mask, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var payload = new byte[(int)length];
        if (!await ReadExactlyOrEofAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] ^= mask[index % 4];
        }

        return new WsFrame(fin, opcode, payload);
    }

    private static async Task<ushort> ReadUInt16Async(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[2];
        if (!await ReadExactlyOrEofAsync(stream, bytes, cancellationToken).ConfigureAwait(false))
        {
            throw new WebSocketProtocolException();
        }

        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static async Task<ulong> ReadUInt64Async(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[8];
        if (!await ReadExactlyOrEofAsync(stream, bytes, cancellationToken).ConfigureAwait(false))
        {
            throw new WebSocketProtocolException();
        }

        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private sealed record WsFrame(bool Fin, int Opcode, byte[] Payload);

    public void Dispose() => _listener.Stop();

    private sealed class WebSocketProtocolException : Exception
    {
    }
}

internal sealed class HttpRequest
{
    private const int MaximumHeaderBytes = 64 * 1_024;
    private const int MaximumHeaderLineBytes = 8 * 1_024;
    private const int MaximumHeaderCount = 100;

    private HttpRequest(
        string method,
        string path,
        QueryClassification query,
        HeaderCollection headers,
        bool connectionClose,
        bool isHead)
    {
        Method = method;
        Path = path;
        Query = query;
        Headers = headers;
        ConnectionClose = connectionClose;
        IsHead = isHead;
    }

    internal string Method { get; }

    internal string Path { get; }

    internal QueryClassification Query { get; }

    internal HeaderCollection Headers { get; }

    internal bool ConnectionClose { get; }

    internal bool IsHead { get; }

    internal static async Task<RequestReadResult?> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var requestLine = await ReadLineAsync(stream, MaximumHeaderLineBytes, cancellationToken).ConfigureAwait(false);
        if (requestLine is null)
        {
            return null;
        }

        if (requestLine.Length == 0)
        {
            return RequestReadResult.Error(400);
        }

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !IsToken(parts[0]) || parts[1].Length == 0 || parts[1].Length > MaximumHeaderLineBytes)
        {
            return RequestReadResult.Error(400);
        }

        if (!string.Equals(parts[2], "HTTP/1.1", StringComparison.Ordinal))
        {
            return RequestReadResult.Error(505);
        }

        var headers = new HeaderCollection();
        var totalHeaderBytes = requestLine.Length;
        while (true)
        {
            var headerLine = await ReadLineAsync(stream, MaximumHeaderLineBytes, cancellationToken).ConfigureAwait(false);
            if (headerLine is null)
            {
                return null;
            }

            totalHeaderBytes += headerLine.Length;
            if (totalHeaderBytes > MaximumHeaderBytes)
            {
                return RequestReadResult.Error(431);
            }

            if (headerLine.Length == 0)
            {
                break;
            }

            if (headers.Count >= MaximumHeaderCount)
            {
                return RequestReadResult.Error(431);
            }

            var separator = headerLine.IndexOf(':');
            if (separator <= 0 || !IsToken(headerLine[..separator]))
            {
                return RequestReadResult.Error(400);
            }

            var name = headerLine[..separator].ToLowerInvariant();
            var value = headerLine[(separator + 1)..].Trim(' ', '\t');
            if (!headers.TryAdd(name, value))
            {
                return RequestReadResult.Error(400);
            }
        }

        var transferEncoding = headers.Get("transfer-encoding");
        var contentLength = headers.Get("content-length");
        if (transferEncoding is not null && contentLength is not null)
        {
            return RequestReadResult.Error(400);
        }

        if (headers.Get("expect") is not null)
        {
            return RequestReadResult.Error(417);
        }

        if (contentLength is not null)
        {
            if (!long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
            {
                return RequestReadResult.Error(400);
            }

            if (length > FixtureOptions.MaximumRequestBytes)
            {
                return RequestReadResult.Error(413);
            }

            if (!await DiscardBytesAsync(stream, length, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }
        else if (transferEncoding is not null)
        {
            if (!HasToken(transferEncoding, "chunked")
                || transferEncoding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length != 1)
            {
                return RequestReadResult.Error(400);
            }

            if (!await DiscardChunkedBodyAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        if (!TryGetPath(parts[1], out var path, out var query))
        {
            return RequestReadResult.Error(400);
        }

        var connectionClose = headers.HasToken("connection", "close");
        return RequestReadResult.Success(new HttpRequest(
            parts[0],
            path,
            query,
            headers,
            connectionClose,
            string.Equals(parts[0], "HEAD", StringComparison.Ordinal)));
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(Math.Min(maximumBytes, 256));
        var singleByte = new byte[1];
        while (bytes.Count < maximumBytes)
        {
            var read = await stream.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : throw new HttpParseException();
            }

            bytes.Add(singleByte[0]);
            if (singleByte[0] == '\n')
            {
                if (bytes.Count < 2 || bytes[^2] != '\r')
                {
                    throw new HttpParseException();
                }

                bytes.RemoveRange(bytes.Count - 2, 2);
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
        }

        throw new HttpParseException();
    }

    private static async Task<bool> DiscardBytesAsync(NetworkStream stream, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1_024];
        while (length > 0)
        {
            var requested = (int)Math.Min(length, buffer.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            length -= read;
        }

        return true;
    }

    private static async Task<bool> DiscardChunkedBodyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        long total = 0;
        while (true)
        {
            var line = await ReadLineAsync(stream, 128, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return false;
            }

            var semicolon = line.IndexOf(';');
            var sizeText = (semicolon < 0 ? line : line[..semicolon]).Trim();
            if (!ulong.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size)
                || size > FixtureOptions.MaximumRequestBytes
                || total + (long)size > FixtureOptions.MaximumRequestBytes)
            {
                throw new HttpParseException();
            }

            if (size == 0)
            {
                while (true)
                {
                    line = await ReadLineAsync(stream, MaximumHeaderLineBytes, cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        return false;
                    }

                    if (line.Length == 0)
                    {
                        return true;
                    }
                }
            }

            if (!await DiscardBytesAsync(stream, (long)size, cancellationToken).ConfigureAwait(false)
                || await ReadLineAsync(stream, 2, cancellationToken).ConfigureAwait(false) != string.Empty)
            {
                return false;
            }

            total += (long)size;
        }
    }

    private static bool TryGetPath(string target, out string path, out QueryClassification query)
    {
        query = default;
        path = string.Empty;
        if (target == "*")
        {
            path = target;
            return true;
        }

        string pathAndQuery = target;
        if (!target.StartsWith('/'))
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri)
                || (absoluteUri.Scheme != Uri.UriSchemeHttp && absoluteUri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            pathAndQuery = absoluteUri.PathAndQuery;
        }

        var querySeparator = pathAndQuery.IndexOf('?');
        var rawPath = querySeparator < 0 ? pathAndQuery : pathAndQuery[..querySeparator];
        var rawQuery = querySeparator < 0 ? string.Empty : pathAndQuery[(querySeparator + 1)..];
        if (rawPath.Length == 0 || rawPath[0] != '/' || rawPath.Contains('\r') || rawPath.Contains('\n'))
        {
            return false;
        }

        path = rawPath;
        query = QueryClassification.From(rawQuery, querySeparator >= 0);
        return true;
    }

    private static bool HasToken(string? value, string token)
    {
        return value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => string.Equals(item, token, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool IsToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character <= 32 || character >= 127 || "()<>@,;:\\\"/[]?={} \t".Contains(character))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class HttpParseException : Exception
    {
    }
}

internal sealed class RequestReadResult
{
    private RequestReadResult(HttpRequest? request, int? errorStatusCode)
    {
        Request = request;
        ErrorStatusCode = errorStatusCode;
    }

    internal HttpRequest? Request { get; }

    internal int? ErrorStatusCode { get; }

    internal static RequestReadResult Success(HttpRequest request) => new(request, null);

    internal static RequestReadResult Error(int statusCode) => new(null, statusCode);
}

internal sealed class HeaderCollection
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    internal int Count => _values.Count;

    internal bool TryAdd(string name, string value) => _values.TryAdd(name, value);

    internal bool Contains(string name) => _values.ContainsKey(name);

    internal string? Get(string name) => _values.GetValueOrDefault(name);

    internal bool HasToken(string name, string token)
    {
        var value = Get(name);
        return value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => string.Equals(item, token, StringComparison.OrdinalIgnoreCase)) == true;
    }
}

internal readonly record struct QueryClassification(
    bool Present,
    int ParameterCount,
    bool HasEmptyParameter,
    bool HasKeylessParameter,
    bool HasEmptyValue,
    bool HasPercentEncoding)
{
    internal static QueryClassification From(string value, bool present)
    {
        if (!present)
        {
            return default;
        }

        if (value.Length == 0)
        {
            return new QueryClassification(true, 0, false, false, false, false);
        }

        var segments = value.Split('&');
        var parameterCount = 0;
        var hasEmptyParameter = false;
        var hasKeylessParameter = false;
        var hasEmptyValue = false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                hasEmptyParameter = true;
                continue;
            }

            parameterCount++;
            var equals = segment.IndexOf('=');
            if (equals < 0)
            {
                hasKeylessParameter = true;
            }
            else if (equals == segment.Length - 1)
            {
                hasEmptyValue = true;
            }
        }

        return new QueryClassification(
            true,
            parameterCount,
            hasEmptyParameter,
            hasKeylessParameter,
            hasEmptyValue,
            value.Contains('%'));
    }
}
