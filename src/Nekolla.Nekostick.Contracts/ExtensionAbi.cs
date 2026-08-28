using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies the stable in-process extension ABI generation.</summary>
public static class ExtensionAbi
{
    /// <summary>Gets the minimum host API version that exposes the API 1.3 sibling bridge.</summary>
    public static HostApiVersion Api13Version { get; } = new(1, 3, 1);

    /// <summary>Gets the current ABI version used by extension entrypoints.</summary>
    public static HostApiVersion Version { get; } = Api13Version;

    /// <summary>Determines whether a host API version can satisfy an extension ABI requirement.</summary>
    /// <param name="required">The required version.</param>
    /// <param name="host">The host version.</param>
    /// <returns><see langword="true" /> when the major generation matches and the host is not older.</returns>
    public static bool IsCompatible(HostApiVersion required, HostApiVersion host) =>
        required.Major == host.Major && host >= required;

    /// <summary>Determines whether the negotiated host exposes the API 1.3 sibling bridge.</summary>
    /// <param name="host">The negotiated host API version.</param>
    /// <returns><see langword="true" /> only for a compatible API 1.3-or-later host in major generation 1.</returns>
    public static bool IsApi13Supported(HostApiVersion host) => IsCompatible(Api13Version, host);
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
/// <summary>Identifies one node-local core event published by the Host.</summary>
public enum ExtensionCoreEventKind
{
    /// <summary>An immutable configuration snapshot was applied.</summary>
    ConfigurationSnapshotApplied,

    /// <summary>A route changed in the active snapshot.</summary>
    RouteChanged,

    /// <summary>A supervised service changed state.</summary>
    ServiceStateChanged,

    /// <summary>A durable port lease changed state.</summary>
    PortLeaseChanged,

    /// <summary>An extension changed lifecycle state.</summary>
    ExtensionStateChanged
}

/// <summary>Contains one immutable node-local core event.</summary>
public sealed record ExtensionCoreEvent
{
    /// <summary>Creates a versioned core event.</summary>
    /// <param name="kind">The stable core event kind.</param>
    /// <param name="version">The event schema version.</param>
    /// <param name="payloadJson">The bounded JSON payload.</param>
    public ExtensionCoreEvent(ExtensionCoreEventKind kind, int version, string payloadJson)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        if (payloadJson is null || payloadJson.Length > 1024 * 1024)
        {
            throw new ArgumentException("An event payload is invalid.", nameof(payloadJson));
        }

        Kind = kind;
        Version = version;
        PayloadJson = payloadJson;
    }

    /// <summary>Gets the stable event kind.</summary>
    public ExtensionCoreEventKind Kind { get; }

    /// <summary>Gets the event schema version.</summary>
    public int Version { get; }

    /// <summary>Gets the immutable JSON payload.</summary>
    public string PayloadJson { get; }
}

/// <summary>Exposes startup-only typed exchange over approved shared contract types.</summary>
public interface IExtensionContractRegistry
{
    /// <summary>Exports one strongly typed implementation for a manifest declaration.</summary>
    /// <typeparam name="TContract">The approved shared contract type.</typeparam>
    /// <param name="contractId">The declared stable contract ID.</param>
    /// <param name="implementation">The implementation instance.</param>
    /// <returns><see langword="true" /> when the declaration and type identity match.</returns>
    bool TryExport<TContract>(string contractId, TContract implementation)
        where TContract : class;

    /// <summary>Imports one strongly typed implementation for a manifest declaration.</summary>
    /// <typeparam name="TContract">The approved shared contract type.</typeparam>
    /// <param name="contractId">The declared stable contract ID.</param>
    /// <param name="contract">The resolved implementation when available.</param>
    /// <returns><see langword="true" /> when a compatible provider was available during startup.</returns>
    bool TryImport<TContract>(string contractId, out TContract? contract)
        where TContract : class;
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

/// <summary>Writes bounded custom text attributed to the calling extension by the Host.</summary>
/// <remarks>
/// The extension supplies only level and text. It cannot supply or impersonate an extension ID;
/// the Host binds identity at the implementation boundary and may emit structured attribution.
/// </remarks>
public interface IExtensionLogWriter
{
    /// <summary>Writes one bounded custom text message.</summary>
    /// <param name="level">The safe logger severity.</param>
    /// <param name="text">The non-sensitive custom text.</param>
    void WriteText(ExtensionLogLevel level, string text);
}


/// <summary>Registers stable handler IDs during extension startup.</summary>
public interface IExtensionRegistration
{
    /// <summary>Attempts to register one handler ID.</summary>
    bool TryRegisterHandler(IExtensionHandler handler);

    /// <summary>Attempts to register the sole global fallback.</summary>
    bool TryRegisterFallback(IExtensionFallback fallback);

    /// <summary>Attempts to unregister one handler ID owned by this extension.</summary>
    /// <remarks>The operation is a nonblocking future-dispatch tombstone; an active invocation may finish.</remarks>
    /// <param name="handlerId">The stable handler identifier.</param>
    /// <returns><see langword="true" /> when the handler was tombstoned for future dispatch.</returns>
    bool TryUnregisterHandler(string handlerId);

    /// <summary>Attempts to unregister this extension's fallback.</summary>
    /// <remarks>The operation is a nonblocking future-dispatch tombstone; an active invocation may finish.</remarks>
    /// <returns><see langword="true" /> when the fallback was tombstoned for future dispatch.</returns>
    bool TryUnregisterFallback();
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
