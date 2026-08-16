using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Nekolla.Nekostick.Tests.Fixtures.Microservice;

internal sealed partial class FixtureServer
{
    private const int MaximumWebSocketMessageBytes = 1 * 1_024 * 1_024;
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

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

    private sealed class WebSocketProtocolException : Exception
    {
    }
}
