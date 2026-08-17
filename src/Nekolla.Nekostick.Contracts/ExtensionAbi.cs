using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies the stable in-process extension ABI generation.</summary>
public static class ExtensionAbi
{
    /// <summary>Gets the current ABI version used by extension entrypoints.</summary>
    public static HostApiVersion Version { get; } = new(1, 0, 0);

    /// <summary>Determines whether a host API version can satisfy an extension ABI requirement.</summary>
    /// <param name="required">The required version.</param>
    /// <param name="host">The host version.</param>
    /// <returns><see langword="true" /> when the major generation matches and the host is not older.</returns>
    public static bool IsCompatible(HostApiVersion required, HostApiVersion host) =>
        required.Major == host.Major && host >= required;
}

/// <summary>Describes the reason supplied to the sole extension fallback.</summary>
public enum ExtensionFallbackReason
{
    /// <summary>No route matched the request.</summary>
    NoRoute,

    /// <summary>The request host did not match a route.</summary>
    HostMismatch,

    /// <summary>The request method did not match a route.</summary>
    MethodMismatch,

    /// <summary>A static target was not found.</summary>
    StaticNotFound,

    /// <summary>A static target did not contain its configured index.</summary>
    StaticIndexMissing
}

/// <summary>Contains an immutable, framework-neutral request passed to one extension handler.</summary>
public sealed class ExtensionHandlerRequest
{
    /// <summary>Creates a request value from bounded host-owned data.</summary>
    /// <param name="method">The uppercase or original HTTP method text.</param>
    /// <param name="path">The normalized request path.</param>
    /// <param name="headers">The request headers.</param>
    /// <param name="body">The request body bytes.</param>
    /// <param name="isHttps">Whether the request arrived over HTTPS.</param>
    public ExtensionHandlerRequest(
        string method,
        string path,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
        ReadOnlyMemory<byte> body = default,
        bool isHttps = false)
    {
        Method = RequireText(method, nameof(method), 32);
        Path = RequireText(path, nameof(path), 8192);
        Headers = CopyHeaders(headers);
        Body = body.IsEmpty ? ImmutableArray<byte>.Empty : ImmutableArray.CreateRange(body.ToArray());
        IsHttps = isHttps;
    }

    /// <summary>Gets the request method.</summary>
    public string Method { get; }

    /// <summary>Gets the normalized request path.</summary>
    public string Path { get; }

    /// <summary>Gets immutable request headers and values.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>> Headers { get; }

    /// <summary>Gets an immutable copy of the request body.</summary>
    public ImmutableArray<byte> Body { get; }

    /// <summary>Gets whether the request arrived over HTTPS.</summary>
    public bool IsHttps { get; }


    private static string RequireText(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ArgumentException("The request value is invalid.", parameterName);
        }

        return value;
    }

    private static ImmutableDictionary<string, ImmutableArray<string>> CopyHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers)
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
                throw new ArgumentException("The request headers are invalid.", nameof(headers));
            }

            var values = pair.Value?.ToImmutableArray() ?? throw new ArgumentException("The request headers are invalid.", nameof(headers));
            if (values.Any(static value => value is null || value.Length > 16 * 1024))
            {
                throw new ArgumentException("The request headers are invalid.", nameof(headers));
            }

            result.Add(pair.Key, values);
        }

        return result.ToImmutable();
    }
}

/// <summary>Contains the immutable response returned by one extension handler.</summary>
public sealed class ExtensionHandlerResponse
{
    /// <summary>Creates a response value.</summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="headers">The response headers.</param>
    /// <param name="body">The response body bytes.</param>
    public ExtensionHandlerResponse(
        int statusCode,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
        ReadOnlyMemory<byte> body = default)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        StatusCode = statusCode;
        Headers = CopyHeaders(headers);
        Body = body.IsEmpty ? ImmutableArray<byte>.Empty : ImmutableArray.CreateRange(body.ToArray());
    }

    /// <summary>Gets the response status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets immutable response headers and values.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>> Headers { get; }

    /// <summary>Gets an immutable copy of the response body.</summary>
    public ImmutableArray<byte> Body { get; }

    private static ImmutableDictionary<string, ImmutableArray<string>> CopyHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers)
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
                throw new ArgumentException("The response headers are invalid.", nameof(headers));
            }

            var values = pair.Value?.ToImmutableArray() ?? throw new ArgumentException("The response headers are invalid.", nameof(headers));
            if (values.Any(static value => value is null || value.Length > 16 * 1024))
            {
                throw new ArgumentException("The response headers are invalid.", nameof(headers));
            }

            result.Add(pair.Key, values);
        }

        return result.ToImmutable();
    }
}

/// <summary>Contains a fallback request and its no-match reason.</summary>
public sealed class ExtensionFallbackRequest
{
    /// <summary>Creates a fallback request.</summary>
    public ExtensionFallbackRequest(ExtensionHandlerRequest request, ExtensionFallbackReason reason)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Reason = reason;
    }

    /// <summary>Gets the original immutable request.</summary>
    public ExtensionHandlerRequest Request { get; }

    /// <summary>Gets the stable no-match reason.</summary>
    public ExtensionFallbackReason Reason { get; }
}

/// <summary>Represents the safe result of fallback evaluation.</summary>
public readonly record struct ExtensionFallbackResult
{
    private ExtensionFallbackResult(bool handled, ExtensionHandlerResponse? response)
    {
        Handled = handled;
        Response = response;
    }

    /// <summary>Gets whether the fallback handled the request.</summary>
    public bool Handled { get; }

    /// <summary>Gets the response when <see cref="Handled" /> is true.</summary>
    public ExtensionHandlerResponse? Response { get; }

    /// <summary>Gets a result that declines fallback handling.</summary>
    public static ExtensionFallbackResult NotHandled => new(false, null);

    /// <summary>Creates a handled fallback result.</summary>
    /// <param name="response">The response to return.</param>
    /// <returns>A handled result.</returns>
    public static ExtensionFallbackResult HandledResponse(ExtensionHandlerResponse response) =>
        new(true, response ?? throw new ArgumentNullException(nameof(response)));
}

/// <summary>Represents the immutable versioned event delivered to one extension.</summary>
public sealed record ExtensionEvent
{
    /// <summary>Creates an extension event.</summary>
    /// <param name="type">The versioned event type.</param>
    /// <param name="version">The event schema version.</param>
    /// <param name="payloadJson">The immutable JSON payload.</param>
    public ExtensionEvent(string type, int version, string payloadJson)
    {
        Type = string.IsNullOrWhiteSpace(type) || type.Length > 256
            ? throw new ArgumentException("An event type is required.", nameof(type))
            : type;
        Version = version < 1 ? throw new ArgumentOutOfRangeException(nameof(version)) : version;
        PayloadJson = payloadJson is null || payloadJson.Length > 1024 * 1024
            ? throw new ArgumentException("An event payload is invalid.", nameof(payloadJson))
            : payloadJson;
    }

    /// <summary>Gets the stable event type.</summary>
    public string Type { get; }

    /// <summary>Gets the event schema version.</summary>
    public int Version { get; }

    /// <summary>Gets the JSON payload.</summary>
    public string PayloadJson { get; }
}

/// <summary>Identifies safe logger severities exposed to an extension.</summary>
public enum ExtensionLogLevel
{
    /// <summary>Informational lifecycle observation.</summary>
    Information,

    /// <summary>A recoverable extension condition.</summary>
    Warning
}

/// <summary>Identifies safe status values reported by an extension.</summary>
public enum ExtensionStatusKind
{
    /// <summary>The extension is healthy.</summary>
    Healthy,

    /// <summary>The extension is degraded.</summary>
    Degraded
}

/// <summary>Contains a safe extension status update.</summary>
public readonly record struct ExtensionStatus
{
    /// <summary>Creates a status update.</summary>
    public ExtensionStatus(ExtensionStatusKind kind, string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128)
        {
            throw new ArgumentException("A status code is required.", nameof(code));
        }

        Kind = kind;
        Code = code;
    }

    /// <summary>Gets the status kind.</summary>
    public ExtensionStatusKind Kind { get; }

    /// <summary>Gets the safe status code.</summary>
    public string Code { get; }
}
/// <summary>Reads the immutable settings supplied by the Host snapshot.</summary>
public interface IExtensionSettingsReader
{
    /// <summary>Gets the current versioned settings or <see langword="null" /> when none exist.</summary>
    ExtensionSettingsConfiguration? Settings { get; }
}

/// <summary>Schedules bounded extension-owned background work.</summary>
public interface IExtensionTaskScheduler
{
    /// <summary>Starts one tracked task that is cancelled during extension stop.</summary>
    /// <param name="taskName">A non-sensitive task category.</param>
    /// <param name="callback">The extension callback.</param>
    /// <returns><see langword="true" /> when capacity accepted the task.</returns>
    ValueTask<bool> StartAsync(string taskName, Func<CancellationToken, ValueTask> callback);
}

/// <summary>Publishes and subscribes to the extension-local ordered event stream.</summary>
public interface IExtensionEventPublisher
{
    /// <summary>Attempts to enqueue one event.</summary>
    /// <param name="event">The immutable event.</param>
    /// <returns><see langword="true" /> when the event was queued.</returns>
    bool TryPublish(
        [SuppressMessage(
            "Naming",
            "CA1716:Identifiers should not match keywords",
            Justification = "The escaped event parameter name is retained for the published extension ABI.")]
        ExtensionEvent @event);

    /// <summary>Subscribes one callback to ordered best-effort delivery.</summary>
    /// <param name="callback">The callback invoked serially by the queue.</param>
    /// <returns><see langword="true" /> when the subscription was accepted.</returns>
    bool TrySubscribe(Func<ExtensionEvent, CancellationToken, ValueTask> callback);
}

/// <summary>Reports safe extension status codes.</summary>
public interface IExtensionStatusSink
{
    /// <summary>Reports one non-sensitive status code.</summary>
    void Report(ExtensionStatus status);
}

/// <summary>Reports safe lifecycle log categories.</summary>
public interface IExtensionLogger
{
    /// <summary>Reports one category without accepting arbitrary extension text.</summary>
    void Report(ExtensionLogLevel level, string code);
}

/// <summary>Registers stable handler IDs during extension startup.</summary>
public interface IExtensionRegistration
{
    /// <summary>Attempts to register one handler ID.</summary>
    bool TryRegisterHandler(IExtensionHandler handler);

    /// <summary>Attempts to register the sole global fallback.</summary>
    bool TryRegisterFallback(IExtensionFallback fallback);
}

/// <summary>Provides lifecycle state and registration to an extension entrypoint.</summary>
public interface IExtensionStartContext
{
    /// <summary>Gets whether this start is part of replacement reload.</summary>
    bool Reloading { get; }

    /// <summary>Gets the narrow host bridge.</summary>
    IExtensionHostBridge Host { get; }

    /// <summary>Gets the private registration surface.</summary>
    IExtensionRegistration Registration { get; }
}

/// <summary>Exposes only explicitly approved host capabilities to an extension.</summary>
public interface IExtensionHostBridge
{
    /// <summary>Gets the host API version used for compatibility checks.</summary>
    HostApiVersion ApiVersion { get; }

    /// <summary>Gets read-only versioned extension settings.</summary>
    IExtensionSettingsReader Configuration { get; }

    /// <summary>Gets the bounded extension task scheduler.</summary>
    IExtensionTaskScheduler Tasks { get; }

    /// <summary>Gets the ordered best-effort event publisher.</summary>
    IExtensionEventPublisher Events { get; }

    /// <summary>Gets the safe status sink.</summary>
    IExtensionStatusSink Status { get; }

    /// <summary>Gets the safe logger.</summary>
    IExtensionLogger Logger { get; }
}

/// <summary>Defines the stable extension lifecycle entrypoint.</summary>
public interface IExtensionEntrypoint
{
    /// <summary>Starts the extension and registers handlers before serving begins.</summary>
    ValueTask StartAsync(IExtensionStartContext context, CancellationToken cancellationToken);

    /// <summary>Stops the extension and releases extension-owned resources.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);

    /// <summary>Notifies a replacement after the previous instance has stopped.</summary>
    ValueTask OnPreviousStoppedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
/// <summary>Provides the concise stable name for the extension lifecycle entrypoint.</summary>
public interface IExtensionEntry : IExtensionEntrypoint
{
}

/// <summary>Defines one stable route handler implemented by an extension.</summary>
public interface IExtensionHandler
{
    /// <summary>Gets the globally stable handler ID.</summary>
    string HandlerId { get; }

    /// <summary>Handles one immutable framework-neutral request.</summary>
    ValueTask<ExtensionHandlerResponse> HandleAsync(
        ExtensionHandlerRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Defines the sole optional global extension fallback.</summary>
public interface IExtensionFallback
{
    /// <summary>Evaluates one no-match request.</summary>
    ValueTask<ExtensionFallbackResult> HandleAsync(
        ExtensionFallbackRequest request,
        CancellationToken cancellationToken);
}
