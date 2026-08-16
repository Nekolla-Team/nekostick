using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Describes whether static HTTP execution produced a response or a typed failure.</summary>
public enum StaticHttpExecutionKind
{
    /// <summary>A response plan was produced.</summary>
    Response,

    /// <summary>The request path or request headers were invalid.</summary>
    InvalidRequest,

    /// <summary>The method is outside the static-file method policy.</summary>
    UnsupportedMethod,

    /// <summary>The target was not found.</summary>
    NotFound,

    /// <summary>The target was rejected by an access or safety boundary.</summary>
    Forbidden,

    /// <summary>The target was a directory and listing or its fixed index was unavailable.</summary>
    DirectoryListingDisabled,

    /// <summary>The static target mapping was invalid or changed.</summary>
    InvalidMapping,

    /// <summary>The target could not be accessed.</summary>
    AccessDenied,

    /// <summary>The target changed or became unavailable while opening.</summary>
    TargetChanged,

    /// <summary>The request contains more than one byte range.</summary>
    MultipleRangesNotSupported,

    /// <summary>The request contains an invalid byte range.</summary>
    InvalidRange
}

/// <summary>Contains the HTTP request headers used by the static executor.</summary>
public sealed class StaticHttpRequestHeaders
{
    /// <summary>Creates a set of conditional and range request headers.</summary>
    public StaticHttpRequestHeaders(
        string? ifMatch = null,
        string? ifNoneMatch = null,
        string? ifModifiedSince = null,
        string? range = null)
    {
        IfMatch = ifMatch;
        IfNoneMatch = ifNoneMatch;
        IfModifiedSince = ifModifiedSince;
        Range = range;
    }

    /// <summary>Gets the raw If-Match field value.</summary>
    public string? IfMatch { get; }

    /// <summary>Gets the raw If-None-Match field value.</summary>
    public string? IfNoneMatch { get; }

    /// <summary>Gets the raw If-Modified-Since field value.</summary>
    public string? IfModifiedSince { get; }

    /// <summary>Gets the raw Range field value.</summary>
    public string? Range { get; }

    /// <summary>Gets an empty request-header set.</summary>
    public static StaticHttpRequestHeaders Empty { get; } = new();
}

/// <summary>Configures response cache policy for static HTTP responses.</summary>
public sealed class StaticHttpExecutionOptions
{
    /// <summary>Creates execution options with a safe Cache-Control field value.</summary>
    /// <param name="cacheControl">The configured Cache-Control value, or null to omit it.</param>
    public StaticHttpExecutionOptions(string? cacheControl = "no-cache")
    {
        if (cacheControl is not null && !IsSafeHeaderValue(cacheControl))
        {
            throw new ArgumentException("Cache-Control contains invalid header characters.", nameof(cacheControl));
        }

        CacheControl = cacheControl;
    }

    /// <summary>Gets the configured Cache-Control value.</summary>
    public string? CacheControl { get; }

    /// <summary>Gets the default static-file cache policy.</summary>
    public static StaticHttpExecutionOptions Default { get; } = new();

    internal static bool IsSafeHeaderValue(string value)
    {
        foreach (var character in value)
        {
            if (character > 0x7e || character < 0x20 || character == '\0')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Represents one generated HTTP response header.</summary>
public readonly record struct StaticHttpHeader(string Name, string Value);

/// <summary>Contains generated, safe headers for a static HTTP response.</summary>
public sealed class StaticHttpResponseHeaders
{
    internal StaticHttpResponseHeaders(
        string? contentType,
        long? contentLength,
        string entityTag,
        DateTimeOffset lastModifiedUtc,
        string? contentRange,
        string? cacheControl)
    {
        ContentType = contentType;
        ContentLength = contentLength;
        ETag = entityTag;
        LastModifiedUtc = lastModifiedUtc.ToUniversalTime();
        LastModified = LastModifiedUtc.ToString("R", CultureInfo.InvariantCulture);
        ContentRange = contentRange;
        CacheControl = cacheControl;

        var headers = ImmutableArray.CreateBuilder<StaticHttpHeader>(8);
        AddHeader(headers, "ETag", ETag);
        AddHeader(headers, "Last-Modified", LastModified);
        if (ContentType is not null)
        {
            AddHeader(headers, "Content-Type", ContentType);
        }

        if (ContentLength is not null)
        {
            AddHeader(headers, "Content-Length", ContentLength.Value.ToString(CultureInfo.InvariantCulture));
        }

        AddHeader(headers, "Accept-Ranges", "bytes");
        if (ContentRange is not null)
        {
            AddHeader(headers, "Content-Range", ContentRange);
        }

        if (CacheControl is not null)
        {
            AddHeader(headers, "Cache-Control", CacheControl);
        }

        Values = headers.ToImmutable();
    }

    /// <summary>Gets the representation MIME type, if applicable to the response.</summary>
    public string? ContentType { get; }

    /// <summary>Gets the response Content-Length value, if applicable.</summary>
    public long? ContentLength { get; }

    /// <summary>Gets the entity tag.</summary>
    public string ETag { get; }

    /// <summary>Gets the UTC last-modified timestamp.</summary>
    public DateTimeOffset LastModifiedUtc { get; }

    /// <summary>Gets the HTTP-date Last-Modified value.</summary>
    public string LastModified { get; }

    /// <summary>Gets the Content-Range value, if this is a partial response.</summary>
    public string? ContentRange { get; }

    /// <summary>Gets the configured Cache-Control value, if configured.</summary>
    public string? CacheControl { get; }

    /// <summary>Gets the immutable generated header collection.</summary>
    public ImmutableArray<StaticHttpHeader> Values { get; }

    private static void AddHeader(
        ImmutableArray<StaticHttpHeader>.Builder headers,
        string name,
        string value)
    {
        if (!StaticHttpExecutionOptions.IsSafeHeaderValue(name)
            || !StaticHttpExecutionOptions.IsSafeHeaderValue(value))
        {
            throw new InvalidOperationException("A generated static response header was unsafe.");
        }

        headers.Add(new StaticHttpHeader(name, value));
    }
}

/// <summary>Contains a complete response plan for a safe static file.</summary>
public sealed class StaticHttpResponsePlan : IDisposable
{
    private readonly StaticFileReadHandle _handle;
    private readonly long _bodyOffset;
    private bool _disposed;

    internal StaticHttpResponsePlan(
        int statusCode,
        StaticHttpResponseHeaders headers,
        StaticFileReadHandle handle,
        long bodyOffset,
        long bodyLength,
        bool hasBody)
    {
        StatusCode = statusCode;
        Headers = headers;
        _handle = handle;
        _bodyOffset = bodyOffset;
        BodyLength = bodyLength;
        HasBody = hasBody;
    }

    /// <summary>Gets the HTTP status code selected by the static HTTP policy.</summary>
    public int StatusCode { get; }

    /// <summary>Gets the generated response headers.</summary>
    public StaticHttpResponseHeaders Headers { get; }

    /// <summary>Gets whether this response has a body to copy.</summary>
    public bool HasBody { get; }

    /// <summary>Gets the number of body bytes available to copy.</summary>
    public long BodyLength { get; }

    /// <summary>Copies the response body to a destination without buffering the file in memory.</summary>
    public ValueTask CopyBodyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return HasBody
            ? CopyBodyCoreAsync(destination, cancellationToken)
            : ValueTask.CompletedTask;
    }

    /// <summary>Disposes the safe opened file handle.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _handle.Dispose();
        }
    }

    private async ValueTask CopyBodyCoreAsync(Stream destination, CancellationToken cancellationToken)
    {
        _handle.Stream.Position = _bodyOffset;
        var remaining = BodyLength;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await _handle.Stream.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The static file ended before the response completed.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>Contains either a safe static HTTP response plan or a non-sensitive typed failure.</summary>
public sealed class StaticHttpExecutionResult : IDisposable
{
    private StaticHttpResponsePlan? _response;

    internal StaticHttpExecutionResult(
        StaticHttpExecutionKind kind,
        StaticHttpResponsePlan? response)
    {
        Kind = kind;
        _response = response;
    }

    /// <summary>Gets the execution outcome.</summary>
    public StaticHttpExecutionKind Kind { get; }

    /// <summary>Gets the response plan, or null when execution returned a typed failure.</summary>
    public StaticHttpResponsePlan? Response => _response;

    /// <summary>Gets whether a response plan is available.</summary>
    public bool HasResponse => Kind == StaticHttpExecutionKind.Response && _response is not null;

    internal static StaticHttpExecutionResult Failure(StaticHttpExecutionKind kind) => new(kind, null);

    /// <summary>Disposes the response plan, if one was produced.</summary>
    public void Dispose()
    {
        _response?.Dispose();
        _response = null;
    }

    /// <summary>Returns a non-sensitive representation without request or filesystem details.</summary>
    public override string ToString() => $"StaticHttpExecutionResult:{Kind}";
}
