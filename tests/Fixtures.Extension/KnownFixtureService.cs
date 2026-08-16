namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Provides a disposable service with deterministic behavior for lifecycle tests.</summary>
public sealed class KnownFixtureService : IDisposable, IAsyncDisposable
{
    private readonly IFixtureLifecycleObserver _observer;
    private int _disposed;

    /// <summary>Creates a known fixture service with an injected observer.</summary>
    /// <param name="observer">The instance-scoped lifecycle observer.</param>
    public KnownFixtureService(IFixtureLifecycleObserver observer)
    {
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _observer.RecordConstruction();
    }

    /// <summary>Emits the stable service signal while the service is active.</summary>
    /// <returns>A non-sensitive deterministic signal.</returns>
    public string GetSignal()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return FixtureSignals.KnownService;
    }

    /// <summary>Disposes this service once through the synchronous lifecycle.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _observer.RecordSynchronousDisposal();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes this service once through the asynchronous lifecycle.</summary>
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
