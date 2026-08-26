using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Routing;
using ProxyHeaderRewrite = Nekolla.Nekostick.Proxy.HeaderRewriteConfiguration;
using ProxyHeaderRewriteOperation = Nekolla.Nekostick.Proxy.HeaderRewriteOperation;

namespace Nekolla.Nekostick.Host;

internal sealed partial class HostRouteTargetExecutor
{
    private static readonly string[] StaticRequestHeaderNames =
    [
        "If-Match",
        "If-None-Match",
        "If-Modified-Since",
        "Range"
    ];

    private async ValueTask<RouteTargetExecutionResult> ExecuteStaticAsync(
        HttpContext context,
        RouteMatch match,
        ExecutableRoute executable,
        string? originalRequestPath,
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

        var forwardedPath = ResolveForwardedPath(context, match, originalRequestPath);
        using var execution = StaticHttpExecutor.Execute(
            executable.StaticTarget,
            context.Request.Method,
            forwardedPath,
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
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, "RouteTarget.StaticResponse");
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
        string? originalRequestPath,
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

            var forwardedPath = ResolveForwardedPath(context, match, originalRequestPath);
            var effectiveClientIdentity = MicroserviceHttpTransformer
                .ResolveEffectiveClientIdentity(context, executable.TrustedProxyPolicy);
            var expansionContext = new RequestHeaderExpansionContext(
                effectiveClientIdentity.ClientIp,
                forwardedPath,
                context.Request.Method,
                context.Request.Host.Value ?? string.Empty,
                effectiveClientIdentity);

            var request = new MicroserviceProxyRequest(
                serviceId: target.ServiceId,
                forwardedPath: forwardedPath,
                timeoutPolicy: executable.TimeoutPolicy,
                requestHeaderRewrites: executable.RequestHeaderRewrites,
                responseHeaderRewrites: executable.ResponseHeaderRewrites,
                trustedProxyPolicy: executable.TrustedProxyPolicy,
                headerExpansionContext: expansionContext,
                retryPolicy: executable.RetryPolicy,
                routeId: match.RouteId);
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
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, "RouteTarget.Microservice");
            return RouteTargetExecutionResult.SafeFailure;
        }
    }

    private static string ResolveForwardedPath(
        HttpContext context,
        RouteMatch match,
        string? originalRequestPath)
    {
        var requestPath = context.Request.Path.Value;
        return string.Equals(requestPath, originalRequestPath, StringComparison.Ordinal)
            ? match.ForwardedPath
            : requestPath ?? "/";
    }

    private static RouteTargetExecutionResult MapStaticFailure(StaticHttpExecutionKind kind) => kind switch
    {
        StaticHttpExecutionKind.InvalidRequest
            or StaticHttpExecutionKind.UnsupportedMethod
            or StaticHttpExecutionKind.MultipleRangesNotSupported
            or StaticHttpExecutionKind.InvalidRange => RouteTargetExecutionResult.BadRequest,
        StaticHttpExecutionKind.NotFound => RouteTargetExecutionResult.StaticNotFound,
        StaticHttpExecutionKind.DirectoryListingDisabled => RouteTargetExecutionResult.StaticIndexMissing,
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
