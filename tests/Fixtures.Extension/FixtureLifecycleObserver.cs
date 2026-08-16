namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Receives instance-scoped lifecycle observations from fixture components.</summary>
public interface IFixtureLifecycleObserver
{
    /// <summary>Records one successfully constructed fixture component.</summary>
    void RecordConstruction();

    /// <summary>Records one synchronous disposal.</summary>
    void RecordSynchronousDisposal();

    /// <summary>Records one asynchronous disposal.</summary>
    void RecordAsynchronousDisposal();

    /// <summary>Records one handler invocation.</summary>
    void RecordHandlerInvocation();

    /// <summary>Records one observed cancellation.</summary>
    void RecordCancellationObserved();

    /// <summary>Records one deliberately observed fixture failure.</summary>
    void RecordFailure();
}

/// <summary>Stores thread-safe, resettable lifecycle counts for one fixture scope.</summary>
public sealed class FixtureLifecycleObserver : IFixtureLifecycleObserver
{
    private int _constructionCount;
    private int _synchronousDisposalCount;
    private int _asynchronousDisposalCount;
    private int _handlerInvocationCount;
    private int _cancellationObservedCount;
    private int _failureCount;

    /// <summary>Gets the number of successful fixture constructions.</summary>
    public int ConstructionCount => Volatile.Read(ref _constructionCount);

    /// <summary>Gets the number of synchronous disposals.</summary>
    public int SynchronousDisposalCount => Volatile.Read(ref _synchronousDisposalCount);

    /// <summary>Gets the number of asynchronous disposals.</summary>
    public int AsynchronousDisposalCount => Volatile.Read(ref _asynchronousDisposalCount);

    /// <summary>Gets the number of handler invocations.</summary>
    public int HandlerInvocationCount => Volatile.Read(ref _handlerInvocationCount);

    /// <summary>Gets the number of cancellations observed by long-lived handlers.</summary>
    public int CancellationObservedCount => Volatile.Read(ref _cancellationObservedCount);

    /// <summary>Gets the number of deliberately recorded failures.</summary>
    public int FailureCount => Volatile.Read(ref _failureCount);

    /// <summary>Gets a consistent snapshot of this observer's counters.</summary>
    public FixtureLifecycleSnapshot Snapshot => new(
        ConstructionCount,
        SynchronousDisposalCount,
        AsynchronousDisposalCount,
        HandlerInvocationCount,
        CancellationObservedCount,
        FailureCount);

    /// <summary>Resets all counters for reuse by one isolated fixture scope.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _constructionCount, 0);
        Interlocked.Exchange(ref _synchronousDisposalCount, 0);
        Interlocked.Exchange(ref _asynchronousDisposalCount, 0);
        Interlocked.Exchange(ref _handlerInvocationCount, 0);
        Interlocked.Exchange(ref _cancellationObservedCount, 0);
        Interlocked.Exchange(ref _failureCount, 0);
    }

    /// <inheritdoc />
    public void RecordConstruction() => Interlocked.Increment(ref _constructionCount);

    /// <inheritdoc />
    public void RecordSynchronousDisposal() => Interlocked.Increment(ref _synchronousDisposalCount);

    /// <inheritdoc />
    public void RecordAsynchronousDisposal() => Interlocked.Increment(ref _asynchronousDisposalCount);

    /// <inheritdoc />
    public void RecordHandlerInvocation() => Interlocked.Increment(ref _handlerInvocationCount);

    /// <inheritdoc />
    public void RecordCancellationObserved() => Interlocked.Increment(ref _cancellationObservedCount);

    /// <inheritdoc />
    public void RecordFailure() => Interlocked.Increment(ref _failureCount);
}

/// <summary>Contains one immutable copy of fixture lifecycle counts.</summary>
public sealed class FixtureLifecycleSnapshot
{
    /// <summary>Creates a lifecycle count snapshot.</summary>
    /// <param name="constructionCount">The construction count.</param>
    /// <param name="synchronousDisposalCount">The synchronous disposal count.</param>
    /// <param name="asynchronousDisposalCount">The asynchronous disposal count.</param>
    /// <param name="handlerInvocationCount">The handler invocation count.</param>
    /// <param name="cancellationObservedCount">The observed cancellation count.</param>
    /// <param name="failureCount">The recorded failure count.</param>
    public FixtureLifecycleSnapshot(
        int constructionCount,
        int synchronousDisposalCount,
        int asynchronousDisposalCount,
        int handlerInvocationCount,
        int cancellationObservedCount,
        int failureCount)
    {
        ConstructionCount = constructionCount;
        SynchronousDisposalCount = synchronousDisposalCount;
        AsynchronousDisposalCount = asynchronousDisposalCount;
        HandlerInvocationCount = handlerInvocationCount;
        CancellationObservedCount = cancellationObservedCount;
        FailureCount = failureCount;
    }

    /// <summary>Gets the construction count.</summary>
    public int ConstructionCount { get; }

    /// <summary>Gets the synchronous disposal count.</summary>
    public int SynchronousDisposalCount { get; }

    /// <summary>Gets the asynchronous disposal count.</summary>
    public int AsynchronousDisposalCount { get; }

    /// <summary>Gets the handler invocation count.</summary>
    public int HandlerInvocationCount { get; }

    /// <summary>Gets the observed cancellation count.</summary>
    public int CancellationObservedCount { get; }

    /// <summary>Gets the recorded failure count.</summary>
    public int FailureCount { get; }
}
