namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Provides a long-lived handler that completes only after cancellation.</summary>
public sealed class CancellationAwareFixtureHandler : IAsyncDisposable
{
    private readonly IFixtureLifecycleObserver _observer;
    private int _disposed;

    /// <summary>Creates a cancellation-aware handler with an injected observer.</summary>
    /// <param name="observer">The instance-scoped lifecycle observer.</param>
    public CancellationAwareFixtureHandler(IFixtureLifecycleObserver observer)
    {
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _observer.RecordConstruction();
    }

    /// <summary>Waits indefinitely until the supplied token is canceled.</summary>
    /// <param name="cancellationToken">The token controlling the long-lived operation.</param>
    /// <returns>The stable cancellation observation signal.</returns>
    public async ValueTask<string> WaitForCancellationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _observer.RecordHandlerInvocation();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _observer.RecordCancellationObserved();
            return FixtureSignals.CancellationObserved;
        }

        return FixtureSignals.CancellationObserved;
    }

    /// <summary>Disposes this handler once and records asynchronous disposal.</summary>
    /// <returns>A completed asynchronous disposal operation.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _observer.RecordAsynchronousDisposal();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
