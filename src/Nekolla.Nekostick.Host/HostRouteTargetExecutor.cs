using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.Host;

/// <summary>Executes static, microservice, and staged extension targets from one snapshot.</summary>
internal sealed partial class HostRouteTargetExecutor : ILeasedRouteTargetExecutor
{
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
        var originalRequestPath = context.Request.Path.Value;

        HostRouteEventSession? routeSession = null;
        try
        {
            routeSession = await HostRouteEvents.BeginAsync(
                    context,
                    snapshot,
                    match,
                    cancellationToken)
                .ConfigureAwait(false);
            if (routeSession?.Cancelled == true)
            {
                return RouteTargetExecutionResult.Cancelled;
            }

            var outcome = executable.Configuration.Target switch
            {
                StaticFileRouteTargetConfiguration =>
                    await ExecuteStaticAsync(context, match, executable, originalRequestPath, cancellationToken),
                MicroserviceRouteTargetConfiguration =>
                    await ExecuteMicroserviceAsync(
                        context,
                        snapshot,
                        match,
                        executable,
                        originalRequestPath,
                        cancellationToken),
                ExtensionHandlerRouteTargetConfiguration extension =>
                    await ExecuteExtensionAsync(
                        context,
                        snapshot,
                        extension.HandlerId,
                        executable.Configuration.MaxRequestBodyBytes ??
                            snapshot.Configuration.GlobalSettings.MaxRequestBodyBytes,
                        publicationLease,
                        cancellationToken),
                _ => RouteTargetExecutionResult.SafeFailure
            };
            return await HostRouteEvents.CompleteAsync(context, routeSession, outcome, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            HostRouteEvents.RestoreResponseBody(context, routeSession);
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return RouteTargetExecutionResult.Cancelled;
        }
        catch (Exception)
        {
            HostRouteEvents.RestoreResponseBody(context, routeSession);
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
        long maxBodyBytes,
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
                    maxBodyBytes,
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
}
