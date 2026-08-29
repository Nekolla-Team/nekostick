using System.Collections.Immutable;
using System.IO;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Contains a framework-neutral request whose body is exposed as a stream.</summary>
/// <remarks>
/// The <see cref="BodyStream" /> is owned by the Host pipeline and is valid only while
/// <see cref="IExtensionStreamingHandler.HandleStreamingAsync" /> is executing. The Host disposes the stream
/// after that call returns. Reads from the stream are bounded by the route's <c>MaxRequestBodyBytes</c>
/// enforcement. On streaming routes, route hooks receive an empty body snapshot instead of a copy of this stream.
/// </remarks>
public sealed class ExtensionStreamingRequest
{
    /// <summary>Creates a streaming request value from host-owned data.</summary>
    /// <param name="method">The uppercase or original HTTP method text.</param>
    /// <param name="path">The normalized request path.</param>
    /// <param name="headers">The request headers.</param>
    /// <param name="bodyStream">
    /// The readable Host-owned request body stream. The Host disposes it after
    /// <see cref="IExtensionStreamingHandler.HandleStreamingAsync" /> returns. <see langword="null" /> uses
    /// <see cref="Stream.Null" /> for an empty body.
    /// </param>
    /// <param name="isHttps">Whether the request arrived over HTTPS.</param>
    public ExtensionStreamingRequest(
        string method,
        string path,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
        Stream? bodyStream = null,
        bool isHttps = false)
    {
        Method = ExtensionStreamingContractValidation.RequireText(method, nameof(method), 32);
        Path = ExtensionStreamingContractValidation.RequireText(path, nameof(path), 8192);
        Headers = ExtensionStreamingContractValidation.CopyHeaders(headers, "request");
        BodyStream = bodyStream ?? Stream.Null;
        if (!BodyStream.CanRead)
        {
            throw new ArgumentException("The request body stream must be readable.", nameof(bodyStream));
        }

        IsHttps = isHttps;
    }

    /// <summary>Gets the request method.</summary>
    public string Method { get; }

    /// <summary>Gets the normalized request path.</summary>
    public string Path { get; }

    /// <summary>Gets immutable request headers and values.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>> Headers { get; }

    /// <summary>
    /// Gets the readable Host-owned request body stream. The stream is valid only during the
    /// <see cref="IExtensionStreamingHandler.HandleStreamingAsync" /> call and is disposed by the Host afterward.
    /// Reads are bounded by the route's <c>MaxRequestBodyBytes</c> enforcement. Route hooks receive an empty body
    /// snapshot on streaming routes.
    /// </summary>
    public Stream BodyStream { get; }

    /// <summary>Gets whether the request arrived over HTTPS.</summary>
    public bool IsHttps { get; }
}

/// <summary>Contains the response returned by one streaming extension handler.</summary>
/// <remarks>
/// The handler owns the <see cref="BodyStream" /> while <see cref="IExtensionStreamingHandler.HandleStreamingAsync" />
/// is executing. Ownership transfers to the Host when that call returns; the handler MUST NOT dispose, mutate, or
/// reposition the stream afterward. The Host reads from the stream's current position, copies it after the call,
/// and then disposes it. Handlers buffering into a <see cref="MemoryStream" /> MUST rewind it (<c>Position = 0</c>)
/// before returning. Writing to the response stream supplies the content that commits the response when the Host
/// copies its first byte to the client; no rollback is possible after that first write. If the callback throws or is
/// canceled before returning a response, the response stream is never transferred to the Host; the request stream
/// is still disposed by the Host.
/// </remarks>
public sealed class ExtensionStreamingResponse
{
    /// <summary>Creates a streaming response value.</summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="headers">The response headers.</param>
    /// <param name="bodyStream">
    /// The readable handler-owned response body stream. Ownership transfers to the Host when
    /// <see cref="IExtensionStreamingHandler.HandleStreamingAsync" /> returns; the handler MUST NOT dispose,
    /// mutate, or reposition it afterward. The Host reads from its current position, copies it, and disposes it.
    /// Handlers buffering into a <see cref="MemoryStream" /> MUST rewind it (<c>Position = 0</c>) before returning.
    /// <see langword="null" /> uses <see cref="Stream.Null" /> for an empty body.
    /// </param>
    public ExtensionStreamingResponse(
        int statusCode,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
        Stream? bodyStream = null)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        StatusCode = statusCode;
        Headers = ExtensionStreamingContractValidation.CopyHeaders(headers, "response");
        BodyStream = bodyStream ?? Stream.Null;
        if (!BodyStream.CanRead)
        {
            throw new ArgumentException("The response body stream must be readable.", nameof(bodyStream));
        }
    }

    /// <summary>Gets the response status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets immutable response headers and values.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>> Headers { get; }

    /// <summary>
    /// Gets the readable handler-owned response body stream. Ownership transfers to the Host when the
    /// <see cref="IExtensionStreamingHandler.HandleStreamingAsync" /> call returns; the handler MUST NOT dispose,
    /// mutate, or reposition it afterward. The Host reads from its current position, then copies and disposes the
    /// stream. Handlers buffering into a <see cref="MemoryStream" /> MUST rewind it (<c>Position = 0</c>) before
    /// returning. The first byte copied to the client commits the response, and no rollback is possible after that
    /// write.
    /// </summary>
    public Stream BodyStream { get; }
}

/// <summary>Defines one stable streaming route handler implemented by an extension.</summary>
/// <remarks>
/// The Host owns and disposes the request stream after the callback completes, including when it throws or is
/// canceled. If the callback returns a response, ownership of that response stream transfers to the Host; the
/// handler must not dispose, mutate, or reposition it afterward. The Host reads from its current position, then
/// copies and disposes it. Request reads are bounded by the route's <c>MaxRequestBodyBytes</c> enforcement. On
/// streaming routes, route hooks receive an empty body snapshot. The first response byte copied to the client
/// commits the response; no rollback is possible after that write.
/// </remarks>
public interface IExtensionStreamingHandler
{
    /// <summary>Gets the globally stable handler ID.</summary>
    string HandlerId { get; }

    /// <summary>Handles one framework-neutral request with streaming body access.</summary>
    /// <param name="request">The host-owned streaming request.</param>
    /// <param name="cancellationToken">The cancellation requested by the Host or caller.</param>
    /// <returns>The status, headers, and handler-owned response stream to copy to the client.</returns>
    ValueTask<ExtensionStreamingResponse> HandleStreamingAsync(
        ExtensionStreamingRequest request,
        CancellationToken cancellationToken);
}

internal static class ExtensionStreamingContractValidation
{
    internal static string RequireText(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ArgumentException("The request value is invalid.", parameterName);
        }

        return value;
    }

    internal static ImmutableDictionary<string, ImmutableArray<string>> CopyHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers,
        string side)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result.ToImmutable();
        }

        foreach (var pair in headers)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 || result.ContainsKey(pair.Key))
            {
                throw new ArgumentException($"The {side} headers are invalid.", nameof(headers));
            }

            var values = pair.Value?.ToImmutableArray() ??
                throw new ArgumentException($"The {side} headers are invalid.", nameof(headers));
            if (values.Any(static value => value is null || value.Length > 16 * 1024))
            {
                throw new ArgumentException($"The {side} headers are invalid.", nameof(headers));
            }

            result.Add(pair.Key, values);
        }

        return result.ToImmutable();
    }
}
