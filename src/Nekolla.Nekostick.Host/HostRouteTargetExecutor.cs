using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Routing;
using ProxyHeaderRewrite = Nekolla.Nekostick.Proxy.HeaderRewriteConfiguration;
using ProxyHeaderRewriteOperation = Nekolla.Nekostick.Proxy.HeaderRewriteOperation;

namespace Nekolla.Nekostick.Host;

/// <summary>Executes static, microservice, and staged extension targets from one snapshot.</summary>
internal sealed class HostRouteTargetExecutor : ILeasedRouteTargetExecutor
{
    private static readonly string[] StaticRequestHeaderNames =
    [
        "If-Match",
        "If-None-Match",
        "If-Modified-Since",
        "Range"
    ];

    private readonly MicroserviceHttpExecutor _microserviceExecutor;
    private readonly IHostServiceLifecycleCoordinator? _lifecycleCoordinator;

    internal HostRouteTargetExecutor(
        MicroserviceHttpExecutor microserviceExecutor,
        IHostServiceLifecycleCoordinator? lifecycleCoordinator = null)
    {
        _microserviceExecutor = microserviceExecutor
            ?? throw new ArgumentNullException(nameof(microserviceExecutor));
        _lifecycleCoordinator = lifecycleCoordinator;
    }

    public ValueTask<RouteTargetExecutionResult> ExecuteAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(context, snapshot, match, null, cancellationToken);

    public ValueTask<RouteTargetExecutionResult> ExecuteAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        HostRoutingSnapshotLease publicationLease,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(context, snapshot, match, publicationLease, cancellationToken);

    private async ValueTask<RouteTargetExecutionResult> ExecuteCoreAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        HostRoutingSnapshotLease? publicationLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(match);

        if (!TryGetExecutableRoute(snapshot, match, out var executable))
        {
            return RouteTargetExecutionResult.SafeFailure;
        }

        try
        {
            return executable.Configuration.Target switch
            {
                StaticFileRouteTargetConfiguration =>
                    await ExecuteStaticAsync(context, match, executable, cancellationToken),
                MicroserviceRouteTargetConfiguration =>
                    await ExecuteMicroserviceAsync(context, snapshot, match, executable, cancellationToken),
                ExtensionHandlerRouteTargetConfiguration extension =>
                    await ExecuteExtensionAsync(
                        context,
                        snapshot,
                        extension.HandlerId,
                        publicationLease,
                        cancellationToken),
                _ => RouteTargetExecutionResult.SafeFailure
            };
        }
        catch (OperationCanceledException)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return RouteTargetExecutionResult.Cancelled;
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return RouteTargetExecutionResult.SafeFailure;
        }
    }

    private static async ValueTask<RouteTargetExecutionResult> ExecuteExtensionAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        string handlerId,
        HostRoutingSnapshotLease? publicationLease,
        CancellationToken cancellationToken)
    {
        ExtensionDispatchLease? dispatchLease = publicationLease?.DispatchLease;
        var ownsLease = false;
        if (dispatchLease is null && snapshot.DispatchGeneration is not null)
        {
            dispatchLease = snapshot.DispatchGeneration.TryAcquireLease();
            ownsLease = dispatchLease is not null;
        }

        if (dispatchLease is null)
        {
            return RouteTargetExecutionResult.Unavailable;
        }

        try
        {
            var request = await ExtensionHttpAdapter.CreateRequestAsync(
                    context,
                    snapshot.Configuration.GlobalSettings.MaxRequestBodyBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                return RouteTargetExecutionResult.BadRequest;
            }

            var result = await dispatchLease
                .HandleAsync(handlerId, request, cancellationToken)
                .ConfigureAwait(false);
            return result.State switch
            {
                ExtensionInvocationState.Handled when result.Response is not null =>
                    await ExtensionHttpAdapter.WriteResponseAsync(
                        context,
                        result.Response,
                        cancellationToken).ConfigureAwait(false)
                        ? RouteTargetExecutionResult.Handled
                        : RouteTargetExecutionResult.InternalServerError,
                ExtensionInvocationState.Failed => RouteTargetExecutionResult.InternalServerError,
                ExtensionInvocationState.Unavailable => RouteTargetExecutionResult.Unavailable,
                _ => RouteTargetExecutionResult.Unavailable
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RouteTargetExecutionResult.Cancelled;
        }
        catch
        {
            return RouteTargetExecutionResult.InternalServerError;
        }
        finally
        {
            if (ownsLease)
            {
                dispatchLease.Dispose();
            }
        }
    }

    private static bool TryGetExecutableRoute(
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        out ExecutableRoute executable)
    {
        executable = null!;
        if (!snapshot.ExecutableRoutes.TryGetValue(match.RouteId, out var found))
        {
            return false;
        }

        var target = match.Target;
        executable = found;
        if (executable.Configuration.Id != match.RouteId
            || target is null
            || executable.Configuration.Target.Type != target.Type
            || executable.Configuration.Forwarding.Mode != match.ForwardingMode
            || !string.Equals(
                executable.Configuration.Forwarding.ReplaceTemplate,
                match.ReplaceTemplate,
                StringComparison.Ordinal))
        {
            executable = null!;
            return false;
        }

        return (executable.Configuration.Target, target) switch
        {
            (StaticFileRouteTargetConfiguration configured, { Type: RouteTargetType.StaticFile }) =>
                executable.StaticTarget is not null
                && IsStaticTargetAligned(
                    executable.StaticTarget,
                    configured.RootPath,
                    target.RootPath),
            (MicroserviceRouteTargetConfiguration configured, { Type: RouteTargetType.Microservice }) =>
                target.ServiceId == configured.ServiceId,
            (ExtensionHandlerRouteTargetConfiguration configured, { Type: RouteTargetType.ExtensionHandler }) =>
                string.Equals(configured.HandlerId, target.HandlerId, StringComparison.Ordinal),
            _ => false
        };
    }

    private static async ValueTask<RouteTargetExecutionResult> ExecuteStaticAsync(
        HttpContext context,
        RouteMatch match,
        ExecutableRoute executable,
        CancellationToken cancellationToken)
    {
        if (executable.StaticTarget is null
            || !TryCreateStaticRequestHeaders(
                context.Request,
                executable.RequestHeaderRewrites,
                out var requestHeaders))
        {
            return RouteTargetExecutionResult.BadRequest;
        }

        using var execution = StaticHttpExecutor.Execute(
            executable.StaticTarget,
            context.Request.Method,
            match.ForwardedPath,
            requestHeaders,
            StaticHttpExecutionOptions.Default);

        if (!execution.HasResponse || execution.Response is null)
        {
            return MapStaticFailure(execution.Kind);
        }

        var response = execution.Response;
        context.Response.StatusCode = response.StatusCode;
        foreach (var header in response.Headers.Values)
        {
            context.Response.Headers.Append(header.Name, header.Value);
        }

        try
        {
            await response.CopyBodyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
            return RouteTargetExecutionResult.Handled;
        }
        catch (OperationCanceledException)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return RouteTargetExecutionResult.Handled;
            }

            return RouteTargetExecutionResult.Cancelled;
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return RouteTargetExecutionResult.Handled;
            }

            return RouteTargetExecutionResult.Unavailable;
        }
    }

    private async ValueTask<RouteTargetExecutionResult> ExecuteMicroserviceAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        ExecutableRoute executable,
        CancellationToken cancellationToken)
    {
        if (executable.Configuration.Target is not MicroserviceRouteTargetConfiguration target)
        {
            return RouteTargetExecutionResult.SafeFailure;
        }

        try
        {
            if (_lifecycleCoordinator is not null)
            {
                var readiness = await _lifecycleCoordinator
                    .EnsureReadyAsync(snapshot.Configuration, target.ServiceId, cancellationToken)
                    .ConfigureAwait(false);
                if (!readiness.IsReady)
                {
                    return RouteTargetExecutionResult.Unavailable;
                }
            }

            var effectiveClientIdentity = MicroserviceHttpTransformer
                .ResolveEffectiveClientIdentity(context, executable.TrustedProxyPolicy);
            var expansionContext = new RequestHeaderExpansionContext(
                effectiveClientIdentity.ClientIp,
                match.ForwardedPath,
                context.Request.Method,
                context.Request.Host.Value ?? string.Empty,
                effectiveClientIdentity);

            var request = new MicroserviceProxyRequest(
                serviceId: target.ServiceId,
                forwardedPath: match.ForwardedPath,
                timeoutPolicy: executable.TimeoutPolicy,
                requestHeaderRewrites: executable.RequestHeaderRewrites,
                responseHeaderRewrites: executable.ResponseHeaderRewrites,
                trustedProxyPolicy: executable.TrustedProxyPolicy,
                headerExpansionContext: expansionContext);
            var result = await _microserviceExecutor
                .ExecuteAsync(context, request, cancellationToken)
                .ConfigureAwait(false);

            return result.Disposition switch
            {
                MicroserviceProxyExecutionDisposition.Handled => RouteTargetExecutionResult.Handled,
                MicroserviceProxyExecutionDisposition.Unavailable => RouteTargetExecutionResult.Unavailable,
                MicroserviceProxyExecutionDisposition.BadRequest => RouteTargetExecutionResult.BadRequest,
                MicroserviceProxyExecutionDisposition.BadGateway => RouteTargetExecutionResult.BadGateway,
                MicroserviceProxyExecutionDisposition.GatewayTimeout => RouteTargetExecutionResult.GatewayTimeout,
                MicroserviceProxyExecutionDisposition.Cancelled => RouteTargetExecutionResult.Cancelled,
                _ => RouteTargetExecutionResult.SafeFailure
            };
        }
        catch (OperationCanceledException)
        {
            return RouteTargetExecutionResult.Cancelled;
        }
        catch (Exception)
        {
            return RouteTargetExecutionResult.SafeFailure;
        }
    }

    private static RouteTargetExecutionResult MapStaticFailure(StaticHttpExecutionKind kind) => kind switch
    {
        StaticHttpExecutionKind.InvalidRequest
            or StaticHttpExecutionKind.UnsupportedMethod
            or StaticHttpExecutionKind.MultipleRangesNotSupported
            or StaticHttpExecutionKind.InvalidRange => RouteTargetExecutionResult.BadRequest,
        StaticHttpExecutionKind.NotFound
            or StaticHttpExecutionKind.DirectoryListingDisabled => RouteTargetExecutionResult.NotFound,
        StaticHttpExecutionKind.Forbidden
            or StaticHttpExecutionKind.AccessDenied => RouteTargetExecutionResult.Forbidden,
        StaticHttpExecutionKind.InvalidMapping
            or StaticHttpExecutionKind.TargetChanged => RouteTargetExecutionResult.Unavailable,
        _ => RouteTargetExecutionResult.SafeFailure
    };

    private static bool TryCreateStaticRequestHeaders(
        HttpRequest request,
        ImmutableArray<ProxyHeaderRewrite> rewrites,
        out StaticHttpRequestHeaders result)
    {
        var staged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in StaticRequestHeaderNames)
        {
            if (!request.Headers.TryGetValue(name, out StringValues values))
            {
                continue;
            }

            var copiedValues = new List<string>();
            foreach (var value in values)
            {
                if (value is null)
                {
                    result = StaticHttpRequestHeaders.Empty;
                    return false;
                }

                copiedValues.Add(value);
            }

            if (copiedValues.Count > 0)
            {
                staged[name] = copiedValues;
            }
        }

        foreach (var operation in new[]
        {
            ProxyHeaderRewriteOperation.Remove,
            ProxyHeaderRewriteOperation.Set,
            ProxyHeaderRewriteOperation.Add
        })
        {
            foreach (var rewrite in rewrites)
            {
                if (!IsStaticRequestHeader(rewrite.Name) || rewrite.Operation != operation)
                {
                    continue;
                }

                switch (operation)
                {
                    case ProxyHeaderRewriteOperation.Remove:
                        staged.Remove(rewrite.Name);
                        break;
                    case ProxyHeaderRewriteOperation.Set:
                        if (rewrite.Value is null)
                        {
                            result = StaticHttpRequestHeaders.Empty;
                            return false;
                        }

                        staged[rewrite.Name] = [rewrite.Value];
                        break;
                    case ProxyHeaderRewriteOperation.Add:
                        if (rewrite.Value is null)
                        {
                            result = StaticHttpRequestHeaders.Empty;
                            return false;
                        }

                        if (!staged.TryGetValue(rewrite.Name, out var values))
                        {
                            values = [];
                            staged.Add(rewrite.Name, values);
                        }

                        values.Add(rewrite.Value);
                        break;
                }
            }
        }

        result = new StaticHttpRequestHeaders(
            Join(staged, "If-Match"),
            Join(staged, "If-None-Match"),
            Join(staged, "If-Modified-Since"),
            Join(staged, "Range"));
        return true;
    }

    private static string? Join(
        Dictionary<string, List<string>> staged,
        string name) => staged.TryGetValue(name, out var values) && values.Count > 0
        ? string.Join(',', values)
        : null;

    private static bool IsStaticRequestHeader(string name) =>
        StaticRequestHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsStaticTargetAligned(
        StaticTargetDefinition target,
        string configuredRoot,
        string? matchedRoot)
    {
        if (!string.Equals(configuredRoot, matchedRoot, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(configuredRoot);
            var end = fullPath.Length;
            while (end > 1 && fullPath[end - 1] == '/')
            {
                end--;
            }

            return string.Equals(
                target.RootPath,
                fullPath[..end],
                StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
