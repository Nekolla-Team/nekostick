using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.Host;

/// <summary>Runs route observations and synchronous action hooks at the Host route boundary.</summary>
internal static partial class HostRouteEvents
{
    internal static async ValueTask<HostRouteEventSession?> BeginAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        CancellationToken cancellationToken)
    {
        var generation = snapshot.DispatchGeneration;
        if (generation is null || !generation.HasRouteObservers(match.RouteId))
        {
            return null;
        }

        var hasHooks = generation.HasRouteHooks(match.RouteId);
        ExtensionRouteRequestSnapshot request;
        try
        {
            request = await CreateRequestSnapshotAsync(context, hasHooks, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return hasHooks
                ? new HostRouteEventSession(generation, match.RouteId, Guid.CreateVersion7(), null!, true)
                {
                    Cancelled = true
                }
                : null;
        }

        var session = new HostRouteEventSession(
            generation,
            match.RouteId,
            Guid.CreateVersion7(),
            request,
            hasHooks);
        if (!hasHooks)
        {
            PublishBestEffort(generation, new ExtensionRouteEvent(
                match.RouteId,
                session.CorrelationId,
                ExtensionRouteEventStage.Trigger,
                request));
            return session;
        }

        var trigger = await generation.DispatchRouteHooksAsync(
                match.RouteId,
                session.CorrelationId,
                ExtensionRouteEventStage.Trigger,
                request,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!trigger.Succeeded || trigger.Cancelled || trigger.Request is null)
        {
            session.Cancelled = true;
            return session;
        }

        if (!TryApplyRequest(context, trigger.Request))
        {
            session.Cancelled = true;
            return session;
        }

        session.Request = trigger.Request;
        PublishBestEffort(generation, new ExtensionRouteEvent(
            match.RouteId,
            session.CorrelationId,
            ExtensionRouteEventStage.Trigger,
            trigger.Request));
        session.OriginalResponseBody = context.Response.Body;
        session.ResponseBuffer = new MemoryStream();
        context.Response.Body = session.ResponseBuffer;
        return session;
    }

    internal static async ValueTask<RouteTargetExecutionResult> CompleteAsync(
        HttpContext context,
        HostRouteEventSession? session,
        RouteTargetExecutionResult outcome,
        CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return outcome;
        }

        if (session.Cancelled)
        {
            RestoreResponseBody(context, session);
            return RouteTargetExecutionResult.Cancelled;
        }

        if (!session.HasHooks)
        {
            PublishBestEffort(session.Generation, new ExtensionRouteEvent(
                session.RouteId,
                session.CorrelationId,
                ExtensionRouteEventStage.Return,
                session.Request,
                TryCreateResponseSnapshot(context, null)));
            return outcome;
        }

        if (context.RequestAborted.IsCancellationRequested)
        {
            RestoreResponseBody(context, session);
            return RouteTargetExecutionResult.Cancelled;
        }

        var response = TryCreateResponseSnapshot(context, session.ResponseBuffer);
        if (response is null)
        {
            RestoreResponseBody(context, session);
            context.Response.Clear();
            context.Response.StatusCode = 499;
            context.Abort();
            return RouteTargetExecutionResult.Cancelled;
        }

        var result = await session.Generation.DispatchRouteHooksAsync(
                session.RouteId,
                session.CorrelationId,
                ExtensionRouteEventStage.Return,
                session.Request,
                response,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Cancelled || result.Response is null ||
            !IsValidResponseReplacement(result.Response))
        {
            RestoreResponseBody(context, session);
            context.Response.Clear();
            context.Response.StatusCode = 499;
            context.Abort();
            return RouteTargetExecutionResult.Cancelled;
        }

        PublishBestEffort(session.Generation, new ExtensionRouteEvent(
            session.RouteId,
            session.CorrelationId,
            ExtensionRouteEventStage.Return,
            result.Request,
            result.Response));
        RestoreResponseBody(context, session);
        if (outcome != RouteTargetExecutionResult.Handled)
        {
            return outcome;
        }

        if (!await CommitResponseAsync(context, result.Response, cancellationToken).ConfigureAwait(false))
        {
            context.Response.Clear();
            context.Response.StatusCode = 499;
            context.Abort();
            return RouteTargetExecutionResult.Cancelled;
        }

        return outcome;
    }

    private static void PublishBestEffort(
        ExtensionDispatchGeneration generation,
        ExtensionRouteEvent observation)
    {
        try
        {
            generation.PublishRouteEvent(observation);
        }
        catch
        {
        }
    }

    private static async ValueTask<ExtensionRouteRequestSnapshot> CreateRequestSnapshotAsync(
        HttpContext context,
        bool includeBody,
        CancellationToken cancellationToken)
    {
        var body = Array.Empty<byte>();
        if (includeBody)
        {
            context.Request.EnableBuffering();
            var stream = context.Request.Body;
            var originalPosition = stream.CanSeek ? stream.Position : 0;
            await using var buffer = new MemoryStream();
            var temporary = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(temporary.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await buffer.WriteAsync(temporary.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (buffer.Length > ExtensionRouteSnapshotLimits.MaximumBodyBytes)
                {
                    throw new InvalidOperationException("The route event body exceeded its bound.");
                }
            }

            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }

            body = buffer.ToArray();
        }

        var headers = context.Request.Headers.Select(static pair =>
            new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value.ToArray()!));
        var path = context.Request.PathBase.Add(context.Request.Path).Value ?? "/";
        var query = context.Request.QueryString.Value;
        if (query?.StartsWith('?') == true)
        {
            query = query[1..];
        }

        return new ExtensionRouteRequestSnapshot(
            context.Request.Method,
            path,
            query,
            context.Request.Host.HasValue ? context.Request.Host.Value : null,
            headers,
            body,
            context.Request.IsHttps);
    }

    private static bool TryApplyRequest(HttpContext context, ExtensionRouteRequestSnapshot request)
    {
        if (request.Path.Length == 0 || !request.Path.StartsWith('/') ||
            request.Path.Any(char.IsControl) || request.QueryString?.Contains('?') == true)
        {
            return false;
        }

        var originalMethod = context.Request.Method;
        var originalPathBase = context.Request.PathBase;
        var originalPath = context.Request.Path;
        var originalQuery = context.Request.QueryString;
        var originalHost = context.Request.Host;
        var originalHeaders = context.Request.Headers
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var originalBody = context.Request.Body;
        var originalContentLength = context.Request.ContentLength;
        if (!TryValidateRequestReplacement(context, request))
        {
            return false;
        }

        try
        {
            if (request.Host is not null)
            {
                context.Request.Host = new HostString(request.Host);
            }

            context.Request.Method = request.Method;
            context.Request.PathBase = PathString.Empty;
            context.Request.Path = request.Path;
            context.Request.QueryString = string.IsNullOrEmpty(request.QueryString)
                ? QueryString.Empty
                : new QueryString("?" + request.QueryString);

            context.Request.Headers.Clear();
            foreach (var header in originalHeaders)
            {
                if (IsProtectedRouteHeader(header.Key))
                {
                    context.Request.Headers[header.Key] = header.Value;
                }
            }

            foreach (var header in request.Headers)
            {
                if (!IsProtectedRouteHeader(header.Key))
                {
                    context.Request.Headers[header.Key] = header.Value.ToArray();
                }
            }

            context.Request.ContentLength = request.Body.Length;
            context.Request.Body = new MemoryStream(request.Body.ToArray(), writable: false);
            return true;
        }
        catch
        {
            context.Request.Method = originalMethod;
            context.Request.PathBase = originalPathBase;
            context.Request.Path = originalPath;
            context.Request.QueryString = originalQuery;
            context.Request.Host = originalHost;
            context.Request.Headers.Clear();
            foreach (var header in originalHeaders)
            {
                context.Request.Headers[header.Key] = header.Value;
            }

            context.Request.ContentLength = originalContentLength;
            var replacementBody = context.Request.Body;
            context.Request.Body = originalBody;
            if (!ReferenceEquals(replacementBody, originalBody))
            {
                replacementBody.Dispose();
            }

            return false;
        }
    }

    private static ExtensionRouteResponseSnapshot? TryCreateResponseSnapshot(
        HttpContext context,
        MemoryStream? body)
    {
        try
        {
            var bytes = body is null ? Array.Empty<byte>() : body.ToArray();
            if (bytes.Length > ExtensionRouteSnapshotLimits.MaximumBodyBytes)
            {
                return null;
            }

            var headers = context.Response.Headers.Select(static pair =>
                new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value.ToArray()!));
            return new ExtensionRouteResponseSnapshot(context.Response.StatusCode, headers, bytes);
        }
        catch
        {
            return null;
        }
    }

    internal static void RestoreResponseBody(HttpContext context, HostRouteEventSession? session)
    {
        if (session?.OriginalResponseBody is not { } originalBody)
        {
            return;
        }

        context.Response.Body = originalBody;
        session.ResponseBuffer?.Dispose();
        session.ResponseBuffer = null;
    }

    private static async ValueTask<bool> CommitResponseAsync(
        HttpContext context,
        ExtensionRouteResponseSnapshot response,
        CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted || !IsValidResponseReplacement(response))
        {
            return false;
        }

        try
        {
            context.Response.StatusCode = response.StatusCode;
            context.Response.Headers.Clear();
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            if (!response.Body.IsDefaultOrEmpty)
            {
                await context.Response.Body.WriteAsync(response.Body.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class HostRouteEventSession
{
    internal HostRouteEventSession(
        ExtensionDispatchGeneration generation,
        Guid routeId,
        Guid correlationId,
        ExtensionRouteRequestSnapshot request,
        bool hasHooks)
    {
        Generation = generation;
        RouteId = routeId;
        CorrelationId = correlationId;
        Request = request;
        HasHooks = hasHooks;
    }

    internal ExtensionDispatchGeneration Generation { get; }
    internal Guid RouteId { get; }
    internal Guid CorrelationId { get; }
    internal ExtensionRouteRequestSnapshot Request { get; set; }
    internal bool HasHooks { get; }
    internal bool Cancelled { get; set; }
    internal Stream? OriginalResponseBody { get; set; }
    internal MemoryStream? ResponseBuffer { get; set; }
}
