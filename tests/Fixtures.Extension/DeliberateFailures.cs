namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Provides a constructor that always fails with a stable safe exception.</summary>
public sealed class DeliberatelyFailingConstructor
{
    /// <summary>Throws the deterministic constructor failure.</summary>
    public DeliberatelyFailingConstructor()
    {
        throw new InvalidOperationException(FixtureSignals.ConstructorFailure);
    }
}

/// <summary>Provides a handler that always fails with a stable safe exception.</summary>
public sealed class DeliberatelyFailingHandler
{
    private readonly IFixtureLifecycleObserver? _observer;

    /// <summary>Creates a deliberately failing handler without an observer.</summary>
    public DeliberatelyFailingHandler()
    {
    }

    /// <summary>Creates a deliberately failing handler with an injected observer.</summary>
    /// <param name="observer">The instance-scoped lifecycle observer.</param>
    public DeliberatelyFailingHandler(IFixtureLifecycleObserver observer)
    {
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    /// <summary>Records the failure and throws the deterministic handler exception.</summary>
    /// <returns>This method never returns successfully.</returns>
    public string Handle()
    {
        _observer?.RecordFailure();
        throw new InvalidOperationException(FixtureSignals.HandlerFailure);
    }
}
