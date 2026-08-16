namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Provides a safe handler with a stable success signal.</summary>
public sealed class KnownFixtureHandler
{
    private readonly IFixtureLifecycleObserver _observer;
    private int _invoked;

    /// <summary>Creates a known fixture handler with an injected observer.</summary>
    /// <param name="observer">The instance-scoped lifecycle observer.</param>
    public KnownFixtureHandler(IFixtureLifecycleObserver observer)
    {
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _observer.RecordConstruction();
    }

    /// <summary>Emits the stable handler signal once per invocation.</summary>
    /// <returns>A non-sensitive deterministic signal.</returns>
    public string Handle()
    {
        Interlocked.Increment(ref _invoked);
        _observer.RecordHandlerInvocation();
        return FixtureSignals.KnownHandler;
    }

    /// <summary>Gets the number of calls made to this handler instance.</summary>
    public int InvocationCount => Volatile.Read(ref _invoked);
}
