using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.Host;

/// <summary>Converts ASP.NET requests and extension responses at the stable ABI boundary.</summary>
internal static class ExtensionHttpAdapter
{
    internal static async ValueTask<ExtensionHandlerRequest?> CreateRequestAsync(
        HttpContext context,
        long maxBodyBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (maxBodyBytes <= 0)
        {
            return null;
        }

        var headers = context.Request.Headers
            .Select(static pair => new KeyValuePair<string, IEnumerable<string>>(
                pair.Key,
                pair.Value.ToArray().Select(static value => value!)));
        byte[] body;
        try
        {
            if (context.Request.ContentLength is long contentLength && contentLength > maxBodyBytes)
            {
                return null;
            }

            context.Request.EnableBuffering();
            var stream = context.Request.Body;
            var originalPosition = stream.CanSeek ? stream.Position : 0;
            await using var buffer = new MemoryStream();
            var temporary = new byte[16 * 1024];
            var total = 0L;
            while (true)
            {
                var read = await stream.ReadAsync(temporary.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxBodyBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(temporary.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            body = buffer.ToArray();
            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        var path = context.Request.PathBase.Add(context.Request.Path).Value;
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        path += context.Request.QueryString.Value;
        try
        {
            return new ExtensionHandlerRequest(
                context.Request.Method,
                path,
                headers,
                body,
                context.Request.IsHttps);
        }
        catch
        {
            return null;
        }
    }

    internal static async ValueTask<bool> WriteResponseAsync(
        HttpContext context,
        ExtensionHandlerResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(response);
        if (context.Response.HasStarted)
        {
            context.Abort();
            return false;
        }

        try
        {
            context.Response.Headers.Clear();
            context.Response.StatusCode = response.StatusCode;
            foreach (var pair in response.Headers)
            {
                context.Response.Headers.Append(
                    pair.Key,
                    new StringValues(pair.Value.ToArray()));
            }

            if (!response.Body.IsDefaultOrEmpty)
            {
                await context.Response.Body
                    .WriteAsync(response.Body.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return false;
        }
        catch
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return false;
        }
    }
}
/// <summary>Invokes the sole staged extension fallback for safe 404 candidates.</summary>
internal sealed class ExtensionRouteFallbackDispatcher : ILeasedRouteFallbackDispatcher
{
    public ValueTask<bool> TryDispatchAsync(HttpContext context, RouteNoMatchReason reason) =>
        ValueTask.FromResult(false);

    public async ValueTask<bool> TryDispatchAsync(
        HttpContext context,
        RouteNoMatchReason reason,
        HostRoutingSnapshotLease publicationLease)
    {
        var fallbackReason = reason switch
        {
            RouteNoMatchReason.NoRoute => ExtensionFallbackReason.NoRoute,
            RouteNoMatchReason.HostMismatch => ExtensionFallbackReason.HostMismatch,
            RouteNoMatchReason.MethodMismatch => ExtensionFallbackReason.MethodMismatch,
            RouteNoMatchReason.StaticNotFound => ExtensionFallbackReason.StaticNotFound,
            RouteNoMatchReason.StaticIndexMissing => ExtensionFallbackReason.StaticIndexMissing,
            _ => (ExtensionFallbackReason?)null
        };
        if (fallbackReason is null || publicationLease.DispatchLease is null)
        {
            return false;
        }

        var request = await ExtensionHttpAdapter.CreateRequestAsync(
                context,
                publicationLease.Snapshot.Configuration.GlobalSettings.MaxRequestBodyBytes,
                context.RequestAborted)
            .ConfigureAwait(false);
        if (request is null)
        {
            return false;
        }

        var result = await publicationLease.DispatchLease
            .HandleFallbackAsync(request, fallbackReason.Value, context.RequestAborted)
            .ConfigureAwait(false);
        if (result.State != ExtensionInvocationState.Handled || result.Response is null)
        {
            return false;
        }

        return await ExtensionHttpAdapter.WriteResponseAsync(
                context,
                result.Response,
                context.RequestAborted)
            .ConfigureAwait(false);
    }
}
