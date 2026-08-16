using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Nekolla.Nekostick.Tests.Fixtures.Microservice;

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
