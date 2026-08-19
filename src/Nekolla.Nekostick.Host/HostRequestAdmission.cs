using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.Host;

internal enum HostRequestAdmissionFailureKind
{
    None,
    Concurrency,
    RateLimit,
    RequestBody,
    RequestHeaders,
    RequestReadTimeout
}

internal readonly record struct HostRequestAdmissionFailure(
    HostRequestAdmissionFailureKind Kind,
    int? RetryAfterSeconds = null)
{
    internal int StatusCode => Kind switch
    {
        HostRequestAdmissionFailureKind.RequestBody => StatusCodes.Status413PayloadTooLarge,
        HostRequestAdmissionFailureKind.RequestHeaders => StatusCodes.Status431RequestHeaderFieldsTooLarge,
        HostRequestAdmissionFailureKind.RateLimit or HostRequestAdmissionFailureKind.Concurrency =>
            StatusCodes.Status429TooManyRequests,
        HostRequestAdmissionFailureKind.RequestReadTimeout => StatusCodes.Status408RequestTimeout,
        _ => StatusCodes.Status503ServiceUnavailable
    };

    internal string Message => Kind switch
    {
        HostRequestAdmissionFailureKind.RequestBody => "Payload too large.",
        HostRequestAdmissionFailureKind.RequestHeaders => "Request header fields too large.",
        HostRequestAdmissionFailureKind.RateLimit => "Too many requests.",
        HostRequestAdmissionFailureKind.Concurrency => "Too many concurrent requests.",
        HostRequestAdmissionFailureKind.RequestReadTimeout => "Request timeout.",
        _ => "Service unavailable."
    };
}

internal sealed class HostRequestAdmissionContext
{
    private int _failureKind;
    private int _retryAfterSeconds;

    internal HostRequestAdmissionFailure? Failure
    {
        get
        {
            var kind = (HostRequestAdmissionFailureKind)Volatile.Read(ref _failureKind);
            return kind == HostRequestAdmissionFailureKind.None
                ? null
                : new HostRequestAdmissionFailure(
                    kind,
                    Volatile.Read(ref _retryAfterSeconds) is var retry && retry > 0 ? retry : null);
        }
    }

    internal void RecordFailure(HostRequestAdmissionFailure failure)
    {
        if (failure.Kind == HostRequestAdmissionFailureKind.None)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _failureKind, (int)failure.Kind, 0) == 0 &&
            failure.RetryAfterSeconds is > 0)
        {
            Volatile.Write(ref _retryAfterSeconds, failure.RetryAfterSeconds.Value);
        }
    }
}

internal readonly record struct HostGlobalAdmissionResult(
    HostRequestConcurrencyLease? Lease,
    HostRequestAdmissionFailure? Rejection,
    bool Cancelled)
{
    internal static HostGlobalAdmissionResult Accepted(HostRequestConcurrencyLease lease) =>
        new(lease, null, false);

    internal static HostGlobalAdmissionResult Rejected(HostRequestAdmissionFailure failure) =>
        new(null, failure, false);

    internal static HostGlobalAdmissionResult Canceled() => new(null, null, true);
}

internal readonly record struct HostRouteAdmissionResult(
    HostRequestConcurrencyLease? Lease,
    HostRequestAdmissionFailure? Rejection,
    bool Cancelled)
{
    internal static HostRouteAdmissionResult Accepted(HostRequestConcurrencyLease? lease = null) =>
        new(lease, null, false);

    internal static HostRouteAdmissionResult Rejected(HostRequestAdmissionFailure failure) =>
        new(null, failure, false);

    internal static HostRouteAdmissionResult Canceled() => new(null, null, true);
}

internal sealed class HostRequestConcurrencyLease : IDisposable
{
    private SemaphoreSlim? _semaphore;

    internal HostRequestConcurrencyLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

    public void Dispose()
    {
        var semaphore = Interlocked.Exchange(ref _semaphore, null);
        semaphore?.Release();
    }
}

internal sealed class HostRequestBodyLease : IDisposable
{
    private readonly HttpRequest _request;
    private readonly Stream _originalBody;
    private HostRequestBodyGuard? _guard;
    private int _disposed;

    internal HostRequestBodyLease(HttpRequest request, Stream originalBody, HostRequestBodyGuard guard)
    {
        _request = request;
        _originalBody = originalBody;
        _guard = guard;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var current = _request.Body;
        _request.Body = _originalBody;
        if (!ReferenceEquals(current, _originalBody))
        {
            current.Dispose();
        }

        Interlocked.Exchange(ref _guard, null)?.Dispose();
    }
}

internal readonly record struct HostRequestPreparation(
    HostRequestBodyLease? BodyLease,
    HostRequestAdmissionFailure? Rejection)
{
    internal static HostRequestPreparation Accepted(HostRequestBodyLease bodyLease) => new(bodyLease, null);

    internal static HostRequestPreparation Rejected(HostRequestAdmissionFailure failure) => new(null, failure);
}

/// <summary>Coordinates node-local request admission against one immutable routing snapshot.</summary>
internal sealed class HostRequestAdmission
{
    private readonly ConditionalWeakTable<HostRoutingSnapshot, HostRequestAdmissionState> _states = new();
    private readonly IHostRequestAdmissionClock _clock;

    internal HostRequestAdmission()
        : this(SystemHostRequestAdmissionClock.Instance)
    {
    }

    internal HostRequestAdmission(IHostRequestAdmissionClock clock) =>
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    internal static HostRequestAdmissionContext CreateContext() => new();

    internal async ValueTask<HostGlobalAdmissionResult> TryAcquireGlobalAsync(
        HostRoutingSnapshot snapshot,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);
        if (context.RequestAborted.IsCancellationRequested)
        {
            return HostGlobalAdmissionResult.Canceled();
        }

        var state = GetState(snapshot);
        try
        {
            if (!await state.Concurrency.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
            {
                return HostGlobalAdmissionResult.Rejected(
                    new HostRequestAdmissionFailure(HostRequestAdmissionFailureKind.Concurrency));
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return HostGlobalAdmissionResult.Canceled();
        }

        var lease = new HostRequestConcurrencyLease(state.Concurrency);
        var policy = snapshot.Configuration.GlobalSettings.ClientIpRatePolicy;
        if (policy is null)
        {
            return HostGlobalAdmissionResult.Accepted(lease);
        }

        try
        {
            var result = await state.RateBuckets
                .AcquireAsync("global", GetTcpPeerIdentity(context), policy, context.RequestAborted)
                .ConfigureAwait(false);
            if (result.Cancelled)
            {
                lease.Dispose();
                return HostGlobalAdmissionResult.Canceled();
            }

            if (!result.Acquired)
            {
                lease.Dispose();
                return HostGlobalAdmissionResult.Rejected(new HostRequestAdmissionFailure(
                    HostRequestAdmissionFailureKind.RateLimit,
                    GetRetryAfterSeconds(policy)));
            }

            return HostGlobalAdmissionResult.Accepted(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal async ValueTask<HostRouteAdmissionResult> TryAcquireRouteAsync(
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(context);
        if (context.RequestAborted.IsCancellationRequested)
        {
            return HostRouteAdmissionResult.Canceled();
        }

        if (!snapshot.ExecutableRoutes.TryGetValue(match.RouteId, out var executable))
        {
            return HostRouteAdmissionResult.Accepted();
        }

        var state = GetState(snapshot);
        HostRequestConcurrencyLease? concurrencyLease = null;
        if (executable.Configuration.MaxConcurrentRequests is { } maxConcurrentRequests)
        {
            var semaphore = state.GetRouteConcurrency(match.RouteId, maxConcurrentRequests);
            try
            {
                if (!await semaphore.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
                {
                    return HostRouteAdmissionResult.Rejected(
                        new HostRequestAdmissionFailure(HostRequestAdmissionFailureKind.Concurrency));
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return HostRouteAdmissionResult.Canceled();
            }

            concurrencyLease = new HostRequestConcurrencyLease(semaphore);
        }

        var routePolicy = executable.Configuration.ClientIpRatePolicy;
        if (routePolicy is null)
        {
            // Null means inheritance; the global policy was already charged before matching.
            return HostRouteAdmissionResult.Accepted(concurrencyLease);
        }

        try
        {
            var result = await state.RateBuckets
                .AcquireAsync(
                    "route:" + match.RouteId.ToString("N"),
                    GetRouteIdentity(context, executable.TrustedProxyPolicy),
                    routePolicy,
                    context.RequestAborted)
                .ConfigureAwait(false);
            if (result.Cancelled)
            {
                concurrencyLease?.Dispose();
                return HostRouteAdmissionResult.Canceled();
            }

            if (!result.Acquired)
            {
                concurrencyLease?.Dispose();
                return HostRouteAdmissionResult.Rejected(new HostRequestAdmissionFailure(
                    HostRequestAdmissionFailureKind.RateLimit,
                    GetRetryAfterSeconds(routePolicy)));
            }

            return HostRouteAdmissionResult.Accepted(concurrencyLease);
        }
        catch
        {
            concurrencyLease?.Dispose();
            throw;
        }
    }

    internal HostRequestPreparation PrepareRequest(
        HostRoutingSnapshot snapshot,
        HttpContext context,
        HostRequestAdmissionContext admissionContext,
        RouteConfiguration? routeConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(admissionContext);

        var settings = snapshot.Configuration.GlobalSettings;
        var maxBodyBytes = routeConfiguration?.MaxRequestBodyBytes ?? settings.MaxRequestBodyBytes;
        var maxHeaderBytes = routeConfiguration?.MaxRequestHeaderBytes ?? settings.MaxRequestHeaderBytes;
        var requestReadTimeout = routeConfiguration?.RequestReadTimeout ?? settings.RequestReadTimeout;
        if (GetRequestHeaderBytes(context.Request, maxHeaderBytes) > maxHeaderBytes)
        {
            var failure = new HostRequestAdmissionFailure(HostRequestAdmissionFailureKind.RequestHeaders);
            admissionContext.RecordFailure(failure);
            return HostRequestPreparation.Rejected(failure);
        }

        if (context.Request.ContentLength is long contentLength && contentLength > maxBodyBytes)
        {
            var failure = new HostRequestAdmissionFailure(HostRequestAdmissionFailureKind.RequestBody);
            admissionContext.RecordFailure(failure);
            return HostRequestPreparation.Rejected(failure);
        }

        TrySetKestrelBodyLimit(context, maxBodyBytes);
        var originalBody = context.Request.Body;
        var guard = new HostRequestBodyGuard(
            originalBody,
            maxBodyBytes,
            requestReadTimeout,
            admissionContext,
            _clock,
            context.RequestAborted);
        context.Request.Body = guard;
        return HostRequestPreparation.Accepted(new HostRequestBodyLease(context.Request, originalBody, guard));
    }

    internal static string GetTcpPeerIdentity(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = NormalizeAddress(context.Connection.RemoteIpAddress);
        return address?.ToString() ?? "unknown";
    }

    internal static HostRequestAdmissionFailure? TryGetProtocolFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode switch
            {
                StatusCodes.Status413PayloadTooLarge =>
                    new HostRequestAdmissionFailure(HostRequestAdmissionFailureKind.RequestBody),
                StatusCodes.Status431RequestHeaderFieldsTooLarge =>
                    new HostRequestAdmissionFailure(HostRequestAdmissionFailureKind.RequestHeaders),
                StatusCodes.Status408RequestTimeout =>
                    new HostRequestAdmissionFailure(HostRequestAdmissionFailureKind.RequestReadTimeout),
                _ => null
            }
            : null;
    }

    private HostRequestAdmissionState GetState(HostRoutingSnapshot snapshot) =>
        _states.GetValue(snapshot, key => new HostRequestAdmissionState(
            key.Configuration.GlobalSettings.MaxConcurrentRequests,
            _clock));

    private static string GetRouteIdentity(HttpContext context, TrustedProxyPolicy trustedProxyPolicy)
    {
        var identity = MicroserviceHttpTransformer.ResolveEffectiveClientIdentity(context, trustedProxyPolicy);
        return NormalizeAddress(identity.EffectiveClient)?.ToString() ?? GetTcpPeerIdentity(context);
    }

    private static int? GetRetryAfterSeconds(ClientIpRatePolicyConfiguration policy) =>
        policy.RetryAfterBehavior == RateLimitRetryAfterBehavior.FromReplenishmentPeriod
            ? Math.Max(1, (int)Math.Ceiling(policy.ReplenishmentPeriod.TotalSeconds))
            : null;

    private static IPAddress? NormalizeAddress(IPAddress? address) => address is null
        ? null
        : address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static long GetRequestHeaderBytes(HttpRequest request, long stopAfter)
    {
        long total = 2;
        foreach (var pair in request.Headers)
        {
            total += Encoding.UTF8.GetByteCount(pair.Key) + 4;
            foreach (var value in pair.Value)
            {
                total += Encoding.UTF8.GetByteCount(value ?? string.Empty) + 2;
                if (total > stopAfter)
                {
                    return total;
                }
            }
        }

        return total;
    }

    private static void TrySetKestrelBodyLimit(HttpContext context, long maxBodyBytes)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is null || feature.IsReadOnly)
        {
            return;
        }

        try
        {
            feature.MaxRequestBodySize = maxBodyBytes;
        }
        catch (InvalidOperationException)
        {
            // The parser already started consuming the request; the guarded stream remains active.
        }
    }
}
