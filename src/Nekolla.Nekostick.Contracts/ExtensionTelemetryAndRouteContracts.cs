using System.Collections.Immutable;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies the safe lifecycle state of a supervised service.</summary>
public enum ExtensionServiceLifecycleState
{
    /// <summary>No lifecycle observation is available.</summary>
    Unknown,

    /// <summary>The service is disabled.</summary>
    Disabled,

    /// <summary>The service is starting.</summary>
    Starting,

    /// <summary>The service is running.</summary>
    Running,

    /// <summary>The service is stopping.</summary>
    Stopping,

    /// <summary>The service failed to start or remain healthy.</summary>
    Failed
}

/// <summary>Identifies the safe health state of a supervised service.</summary>
public enum ExtensionServiceHealthState
{
    /// <summary>No health observation is available.</summary>
    Unknown,

    /// <summary>The latest health observation succeeded.</summary>
    Healthy,

    /// <summary>The latest health observation failed.</summary>
    Unhealthy
}

/// <summary>Contains immutable runtime telemetry for one service.</summary>
/// <remarks>
/// This DTO intentionally contains no supervisor, process, socket, framework, or runtime handle.
/// All supplied timestamps are normalized to UTC and must form a nondecreasing observation sequence:
/// process start and health observation cannot occur after the last update.
/// </remarks>
public sealed record ExtensionServiceRuntimeSnapshot
{
    /// <summary>Creates an immutable service runtime telemetry snapshot.</summary>
    /// <param name="serviceId">The stable service identifier.</param>
    /// <param name="processId">The operating-system process ID when one is currently known.</param>
    /// <param name="startedAt">The UTC time at which the current process generation started.</param>
    /// <param name="uptime">The current process generation uptime when it is representable.</param>
    /// <param name="lifecycleState">The safe lifecycle state.</param>
    /// <param name="healthState">The safe health state.</param>
    /// <param name="forwardedRequestCount">The cumulative number of forwarded requests.</param>
    /// <param name="activeForwardedRequestCount">The number of currently forwarded requests.</param>
    /// <param name="lastUpdatedAt">The UTC time at which this telemetry was last updated.</param>
    /// <param name="lastHealthAt">The UTC time of the latest health observation.</param>
    public ExtensionServiceRuntimeSnapshot(
        Guid serviceId,
        int? processId,
        DateTimeOffset? startedAt,
        TimeSpan? uptime,
        ExtensionServiceLifecycleState lifecycleState,
        ExtensionServiceHealthState healthState,
        long forwardedRequestCount,
        long activeForwardedRequestCount,
        DateTimeOffset? lastUpdatedAt,
        DateTimeOffset? lastHealthAt)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
        if (processId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        if (uptime is { } duration && duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(uptime));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(forwardedRequestCount);
        ArgumentOutOfRangeException.ThrowIfNegative(activeForwardedRequestCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(activeForwardedRequestCount, forwardedRequestCount);

        var startedAtUtc = startedAt?.ToUniversalTime();
        var lastUpdatedAtUtc = lastUpdatedAt?.ToUniversalTime();
        var lastHealthAtUtc = lastHealthAt?.ToUniversalTime();
        if (startedAtUtc is { } started && lastUpdatedAtUtc is { } updated && started > updated)
        {
            throw new ArgumentException("The service telemetry timestamps are inconsistent.");
        }

        if (lastHealthAtUtc is { } health && lastUpdatedAtUtc is { } updatedAtHealth && health > updatedAtHealth)
        {
            throw new ArgumentException("The service telemetry timestamps are inconsistent.");
        }

        ProcessId = processId;
        StartedAt = startedAtUtc;
        Uptime = uptime;
        LifecycleState = lifecycleState;
        HealthState = healthState;
        ForwardedRequestCount = forwardedRequestCount;
        ActiveForwardedRequestCount = activeForwardedRequestCount;
        LastUpdatedAt = lastUpdatedAtUtc;
        LastHealthAt = lastHealthAtUtc;
    }

    /// <summary>Gets the stable service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the operating-system process ID, when one is currently known.</summary>
    public int? ProcessId { get; }

    /// <summary>Gets the UTC start time of the current process generation, when known.</summary>
    public DateTimeOffset? StartedAt { get; }

    /// <summary>Gets the current process generation uptime, when representable.</summary>
    public TimeSpan? Uptime { get; }

    /// <summary>Gets the safe lifecycle state.</summary>
    public ExtensionServiceLifecycleState LifecycleState { get; }

    /// <summary>Gets the safe health state.</summary>
    public ExtensionServiceHealthState HealthState { get; }

    /// <summary>Gets the cumulative forwarded request count.</summary>
    public long ForwardedRequestCount { get; }

    /// <summary>Gets the active forwarded request count.</summary>
    public long ActiveForwardedRequestCount { get; }

    /// <summary>Gets the UTC time at which this telemetry was last updated, when known.</summary>
    public DateTimeOffset? LastUpdatedAt { get; }

    /// <summary>Gets the UTC time of the latest health observation, when known.</summary>
    public DateTimeOffset? LastHealthAt { get; }
}

/// <summary>Provides global, read-only supervisor telemetry to an extension.</summary>
/// <remarks>
/// The API exposes the Host-wide safe runtime snapshot set, indexed by stable service ID. An unavailable service is
/// reported through the safe result contract rather than exposing runtime handles.
/// </remarks>
public interface IExtensionSupervisorApi
{
    /// <summary>Reads immutable runtime snapshots for all available services.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The immutable snapshots or a safe error.</returns>
    ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Reads one service runtime snapshot.</summary>
    /// <param name="serviceId">The stable service identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The snapshot when the service is available, or a safe error.</returns>
    ValueTask<ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>> GetAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Exposes stable event names for route observations on the standard extension event bus.</summary>
public static class ExtensionRouteEventTypes
{
    /// <summary>The standard event type for a route trigger observation.</summary>
    public const string Trigger = "route.trigger";

    /// <summary>The standard event type for a route return observation.</summary>
    public const string Return = "route.return";

    /// <summary>The schema version used by route observation events.</summary>
    public const int Version = 1;
}

/// <summary>Defines public bounds enforced by route snapshot implementations.</summary>
public static class ExtensionRouteSnapshotLimits
{
    /// <summary>The maximum copied request or response body length.</summary>
    public const int MaximumBodyBytes = 64 * 1024;

    /// <summary>The maximum normalized host length.</summary>
    public const int MaximumHostLength = 256;

    /// <summary>The maximum number of copied header pairs on one side.</summary>
    public const int MaximumHeaderCount = 128;

    /// <summary>The maximum number of values in one copied header pair.</summary>
    public const int MaximumHeaderValuesPerHeader = 64;

    /// <summary>The maximum length of one copied header value.</summary>
    public const int MaximumHeaderValueLength = 16 * 1024;

    /// <summary>The maximum combined UTF-16 text length of copied headers on one side.</summary>
    public const int MaximumHeaderTextLength = 64 * 1024;
}

/// <summary>Defines public bounds enforced by extension custom text logging.</summary>
public static class ExtensionLogLimits
{
    /// <summary>The maximum custom log text length.</summary>
    public const int MaximumTextLength = 4096;
}

/// <summary>Identifies whether a route observation occurs before or after forwarding.</summary>
public enum ExtensionRouteEventStage
{
    /// <summary>The request matched a route before forwarding.</summary>
    Trigger,

    /// <summary>The forwarded operation returned or completed.</summary>
    Return
}

/// <summary>Contains a bounded immutable request snapshot for route observations and hooks.</summary>
public sealed record ExtensionRouteRequestSnapshot
{
    /// <summary>The maximum request body copied across the extension boundary.</summary>
    public const int MaximumBodyBytes = ExtensionRouteSnapshotLimits.MaximumBodyBytes;

    /// <summary>Creates a bounded immutable request snapshot.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The normalized request path.</param>
    /// <param name="queryString">The query string without the leading question mark.</param>
    /// <param name="host">The normalized host text, when known; null means that the host was unavailable.</param>
    /// <param name="headers">The copied request headers.</param>
    /// <param name="body">The bounded request body bytes.</param>
    /// <param name="isHttps">Whether the request arrived over HTTPS.</param>
    public ExtensionRouteRequestSnapshot(
        string method,
        string path,
        string? queryString = null,
        string? host = null,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
        ReadOnlyMemory<byte> body = default,
        bool isHttps = false)
    {
        Method = RequireText(method, nameof(method), 32);
        Path = RequireText(path, nameof(path), 8192);
        QueryString = RequireOptionalText(queryString, nameof(queryString), 8192);
        Host = RequireOptionalHost(host, nameof(host));
        Headers = CopyHeaders(headers, "request");
        Body = CopyBody(body, nameof(body));
        IsHttps = isHttps;
    }

    internal static string? RequireOptionalHost(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0 || value.Length > ExtensionRouteSnapshotLimits.MaximumHostLength ||
            value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The route snapshot host is invalid.", parameterName);
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

        var totalTextLength = 0L;
        foreach (var pair in headers)
        {
            if (result.Count >= ExtensionRouteSnapshotLimits.MaximumHeaderCount || string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Key.Length > 256 || pair.Key.Any(char.IsControl) || result.ContainsKey(pair.Key))
            {
                throw new ArgumentException($"The {side} snapshot headers are invalid.", nameof(headers));
            }

            if (totalTextLength > ExtensionRouteSnapshotLimits.MaximumHeaderTextLength - pair.Key.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(headers));
            }

            var valueSequence = pair.Value ??
                throw new ArgumentException($"The {side} snapshot headers are invalid.", nameof(headers));
            var values = ImmutableArray.CreateBuilder<string>();
            var valueCount = 0;
            totalTextLength += pair.Key.Length;
            foreach (var value in valueSequence)
            {
                valueCount++;
                if (valueCount > ExtensionRouteSnapshotLimits.MaximumHeaderValuesPerHeader || value is null ||
                    value.Length > ExtensionRouteSnapshotLimits.MaximumHeaderValueLength || value.Any(char.IsControl))
                {
                    throw new ArgumentException($"The {side} snapshot headers are invalid.", nameof(headers));
                }

                if (totalTextLength > ExtensionRouteSnapshotLimits.MaximumHeaderTextLength - value.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(headers));
                }

                totalTextLength += value.Length;
                values.Add(value);
            }

            if (values.Count == 0)
            {
                throw new ArgumentException($"The {side} snapshot headers are invalid.", nameof(headers));
            }

            result.Add(pair.Key, values.ToImmutable());
        }

        return result.ToImmutable();
    }

    /// <summary>Gets the request method.</summary>
    public string Method { get; }

    /// <summary>Gets the normalized request path.</summary>
    public string Path { get; }

    /// <summary>Gets the query string without the leading question mark.</summary>
    public string? QueryString { get; }

    /// <summary>Gets the normalized host text, when known.</summary>
    public string? Host { get; }

    /// <summary>Gets immutable request headers and values.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>> Headers { get; }

    /// <summary>Gets an immutable copy of the bounded request body.</summary>
    public ImmutableArray<byte> Body { get; }

    /// <summary>Gets whether the request arrived over HTTPS.</summary>
    public bool IsHttps { get; }

    internal static string RequireText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("The route snapshot text is invalid.", parameterName);
        }

        return value;
    }

    internal static string? RequireOptionalText(string? value, string parameterName, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("The route snapshot text is invalid.", parameterName);
        }

        return value;
    }

    internal static ImmutableArray<byte> CopyBody(ReadOnlyMemory<byte> body, string parameterName)
    {
        if (body.Length > MaximumBodyBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return body.IsEmpty ? ImmutableArray<byte>.Empty : ImmutableArray.CreateRange(body.ToArray());
    }

}

/// <summary>Contains a bounded immutable response snapshot for route observations and hooks.</summary>
public sealed record ExtensionRouteResponseSnapshot
{
    /// <summary>Creates a bounded immutable response snapshot.</summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="headers">The copied response headers.</param>
    /// <param name="body">The bounded response body bytes.</param>
    public ExtensionRouteResponseSnapshot(
        int statusCode,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
        ReadOnlyMemory<byte> body = default)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        StatusCode = statusCode;
        Headers = ExtensionRouteRequestSnapshot.CopyHeaders(headers, "response");
        Body = ExtensionRouteRequestSnapshot.CopyBody(body, nameof(body));
    }

    /// <summary>Gets the HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets immutable response headers and values.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>> Headers { get; }

    /// <summary>Gets an immutable copy of the bounded response body.</summary>
    public ImmutableArray<byte> Body { get; }
}

