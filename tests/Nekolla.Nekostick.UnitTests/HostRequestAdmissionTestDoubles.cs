using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.UnitTests;

internal sealed class ThrowFirstTargetExecutor : IRouteTargetExecutor
{
    private int _callCount;

    internal int CallCount => Volatile.Read(ref _callCount);

    public ValueTask<RouteTargetExecutionResult> ExecuteAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _callCount) == 1)
        {
            throw new InvalidOperationException("target failure");
        }

        return ValueTask.FromResult(RouteTargetExecutionResult.Handled);
    }
}

internal sealed class CancelFirstTargetExecutor : IRouteTargetExecutor
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callCount;

    internal int CallCount => Volatile.Read(ref _callCount);

    internal Task Started => _started.Task;

    public async ValueTask<RouteTargetExecutionResult> ExecuteAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _callCount) != 1)
        {
            return RouteTargetExecutionResult.Handled;
        }

        _started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return RouteTargetExecutionResult.Handled;
    }
}

internal sealed class StartedResponseFeature : IHttpResponseFeature
{
    public int StatusCode { get; set; } = StatusCodes.Status200OK;

    public string? ReasonPhrase { get; set; }

    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

#pragma warning disable CS0618
    public Stream Body { get; set; } = new MemoryStream();
#pragma warning restore CS0618

    public bool HasStarted => true;

    public void OnStarting(Func<object, Task> callback, object state)
    {
    }

    public void OnCompleted(Func<object, Task> callback, object state)
    {
    }
}

internal sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
{
    public CancellationToken RequestAborted { get; set; }

    internal bool Aborted { get; private set; }

    public void Abort() => Aborted = true;
}
