using System.Collections.Immutable;

using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostServiceLogBufferTests
{
    private const int MaximumOutputChunkBytes = 8 * 1024;
    private static readonly Guid ServiceId = new("0198a1af-6e94-7b25-9732-59c9075b14f6");
    private static readonly ProcessInstanceId FirstGeneration =
        new(Guid.Parse("0198a1af-6e94-7b25-9732-59c9075b1511"));
    private static readonly ProcessInstanceId SecondGeneration =
        new(Guid.Parse("0198a1af-6e94-7b25-9732-59c9075b1512"));
    private static readonly DateTimeOffset EventTime = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task CursorReplayIsOrderedExactlyOnceAcrossGenerations()
    {
        var buffer = new HostServiceLogBuffer(ServiceId, 1_048_576);
        buffer.RecordGenerationStarted(FirstGeneration, 1, EventTime);
        buffer.RecordOutput(
            FirstGeneration,
            1,
            ProcessOutputStream.Stdout,
            new byte[] { (byte)'a' },
            EventTime);
        buffer.RecordProcessExited(FirstGeneration, 1, 0, EventTime);
        buffer.RecordGenerationStarted(SecondGeneration, 2, EventTime);
        buffer.RecordOutput(
            SecondGeneration,
            2,
            ProcessOutputStream.Stderr,
            new byte[] { (byte)'b' },
            EventTime);
        buffer.RecordOutput(
            SecondGeneration,
            2,
            ProcessOutputStream.Stdout,
            new byte[] { (byte)'c' },
            EventTime);

        var sink = new RecordingSink();
        await using var subscription = Subscribe(buffer, sink, sinceSequence: 2);
        subscription.Start();
        await sink.WaitForEntryCountAsync(5, TestContext.Current.CancellationToken);

        var entries = sink.Snapshot();
        Assert.Equal(ExtensionServiceLogEntryKind.CurrentState, entries[0].Kind);
        Assert.Equal(
            new long?[] { 3, 4, 5, 6 },
            entries.Skip(1).Select(static entry => entry.Sequence).ToArray());
        Assert.Equal(
            new[]
            {
                ExtensionServiceLogEntryKind.ProcessExited,
                ExtensionServiceLogEntryKind.GenerationStarted,
                ExtensionServiceLogEntryKind.Output,
                ExtensionServiceLogEntryKind.Output
            },
            entries.Skip(1).Select(static entry => entry.Kind).ToArray());
        Assert.All(entries.Skip(1), static entry => Assert.NotNull(entry.Sequence));
        Assert.Equal(2, entries[2].AttemptNumber);
        Assert.Equal(2, entries[3].AttemptNumber);
        Assert.Equal((byte)'b', entries[3].Data[0]);
        Assert.Equal((byte)'c', entries[4].Data[0]);
        Assert.Equal(0, sink.CompletionCount);
    }


    [Fact]
    public async Task LongRetainedReplayIsNotDroppedByDeliveryQueueCapacity()
    {
        const int replayCount = 200;
        var buffer = new HostServiceLogBuffer(ServiceId, 1_048_576);
        for (var index = 1; index <= replayCount; index++)
        {
            buffer.RecordOutput(
                FirstGeneration,
                1,
                ProcessOutputStream.Stdout,
                new byte[] { (byte)index },
                EventTime);
        }

        var sink = new RecordingSink();
        await using var subscription = Subscribe(buffer, sink);
        subscription.Start();
        await sink.WaitForEntryCountAsync(replayCount + 1, TestContext.Current.CancellationToken);

        var entries = sink.Snapshot();
        Assert.Equal(replayCount + 1, entries.Length);
        Assert.Equal(ExtensionServiceLogEntryKind.CurrentState, entries[0].Kind);
        Assert.DoesNotContain(entries, static entry => entry.Kind == ExtensionServiceLogEntryKind.Gap);
        Assert.Equal(
            Enumerable.Range(1, replayCount).Select(static sequence => (long?)sequence).ToArray(),
            entries.Skip(1).Select(static entry => entry.Sequence).ToArray());
    }

    [Fact]
    public async Task LifecycleAndOutputEntriesShareOneMonotonicSequence()
    {
        var buffer = new HostServiceLogBuffer(ServiceId, 1_048_576);
        buffer.RecordGenerationStarted(FirstGeneration, 1, EventTime);
        buffer.RecordOutput(
            FirstGeneration,
            1,
            ProcessOutputStream.Stdout,
            new byte[] { (byte)'a' },
            EventTime);
        buffer.RecordProcessExited(FirstGeneration, 1, 7, EventTime);
        buffer.RecordGenerationStarted(SecondGeneration, 2, EventTime);
        buffer.RecordOutput(
            SecondGeneration,
            2,
            ProcessOutputStream.Stderr,
            new byte[] { (byte)'b' },
            EventTime);
        buffer.RecordProcessExited(SecondGeneration, 2, 0, EventTime);
        buffer.RecordStartupFailure(
            3,
            null,
            ExtensionServiceFailureStage.Spawn,
            ExtensionServiceFailureCode.ExecutableMissing,
            "executable unavailable",
            EventTime);

        var sink = new RecordingSink();
        await using var subscription = Subscribe(buffer, sink);
        subscription.Start();
        await sink.WaitForEntryCountAsync(8, TestContext.Current.CancellationToken);

        var entries = sink.Snapshot();
        Assert.Equal(ExtensionServiceLogEntryKind.CurrentState, entries[0].Kind);
        var sequenced = entries.Skip(1).ToArray();
        Assert.Equal(
            new long?[] { 1, 2, 3, 4, 5, 6, 7 },
            sequenced.Select(static entry => entry.Sequence).ToArray());
        Assert.Equal(
            new[]
            {
                ExtensionServiceLogEntryKind.GenerationStarted,
                ExtensionServiceLogEntryKind.Output,
                ExtensionServiceLogEntryKind.ProcessExited,
                ExtensionServiceLogEntryKind.GenerationStarted,
                ExtensionServiceLogEntryKind.Output,
                ExtensionServiceLogEntryKind.ProcessExited,
                ExtensionServiceLogEntryKind.StartupFailed
            },
            sequenced.Select(static entry => entry.Kind).ToArray());
        Assert.Equal(7, sequenced[2].ProcessExitCode);
        Assert.Equal(ExtensionServiceFailureStage.Spawn, sequenced[6].FailureStage);
        Assert.Equal(ExtensionServiceFailureCode.ExecutableMissing, sequenced[6].FailureCode);
    }

    [Fact]
    public async Task CursorOlderThanRetainedWindowReportsGapBeforeOldestEntry()
    {
        var buffer = new HostServiceLogBuffer(ServiceId, HostServiceLogBuffer.MaximumDeliveryEntryBytes);
        for (var index = 1; index <= 3; index++)
        {
            buffer.RecordOutput(
                FirstGeneration,
                1,
                ProcessOutputStream.Stdout,
                CreateChunk((byte)index, MaximumOutputChunkBytes),
                EventTime);
        }

        var sink = new RecordingSink();
        await using var subscription = Subscribe(buffer, sink, sinceSequence: 0);
        subscription.Start();
        await sink.WaitForEntryCountAsync(3, TestContext.Current.CancellationToken);

        var entries = sink.Snapshot();
        Assert.Equal(ExtensionServiceLogEntryKind.CurrentState, entries[0].Kind);
        Assert.Equal(ExtensionServiceLogEntryKind.Gap, entries[1].Kind);
        Assert.Equal(1L, entries[1].FirstMissingSequence);
        Assert.Equal(2L, entries[1].LastMissingSequence);
        Assert.Equal(2L, entries[1].Sequence);
        Assert.Equal(ExtensionServiceLogEntryKind.Output, entries[2].Kind);
        Assert.Equal(3L, entries[2].Sequence);
        Assert.Equal((byte)3, entries[2].Data[0]);
    }

    [Fact]
    public async Task ByteBudgetDropsOldestEntriesAndReportsTheMissingRange()
    {
        var buffer = new HostServiceLogBuffer(
            ServiceId,
            2 * HostServiceLogBuffer.MaximumDeliveryEntryBytes);
        var chunk = CreateChunk((byte)'L', MaximumOutputChunkBytes);
        buffer.RecordOutput(FirstGeneration, 1, ProcessOutputStream.Stdout, chunk, EventTime);
        buffer.RecordOutput(FirstGeneration, 1, ProcessOutputStream.Stdout, chunk, EventTime);
        buffer.RecordOutput(FirstGeneration, 1, ProcessOutputStream.Stdout, chunk, EventTime);

        var sink = new RecordingSink();
        await using var subscription = Subscribe(buffer, sink, sinceSequence: 0);
        subscription.Start();
        await sink.WaitForEntryCountAsync(4, TestContext.Current.CancellationToken);

        var entries = sink.Snapshot();
        Assert.Equal(ExtensionServiceLogEntryKind.Gap, entries[1].Kind);
        Assert.Equal(1L, entries[1].FirstMissingSequence);
        Assert.Equal(1L, entries[1].LastMissingSequence);
        Assert.Equal(
            new long?[] { 1, 2, 3 },
            entries.Skip(1).Select(static entry => entry.Sequence).ToArray());
        Assert.Equal((byte)'L', entries[2].Data[0]);
        Assert.Equal((byte)'L', entries[3].Data[0]);
        Assert.Equal(MaximumOutputChunkBytes, entries[2].Data.Length);
        Assert.Equal(MaximumOutputChunkBytes, entries[3].Data.Length);
    }

    [Fact]
    public async Task RemovalTerminationCompletesAfterGenerationExitAndDisposalQuiesces()
    {
        var buffer = new HostServiceLogBuffer(ServiceId, 1_048_576);
        buffer.RecordGenerationStarted(FirstGeneration, 1, EventTime);
        var detachedCount = 0;
        var quiescedCount = 0;
        var sink = new RecordingSink();
        await using var subscription = Subscribe(
            buffer,
            sink,
            onDetached: _ => Interlocked.Increment(ref detachedCount),
            onQuiesced: _ => Interlocked.Increment(ref quiescedCount));
        subscription.Start();
        await sink.WaitForEntryCountAsync(2, TestContext.Current.CancellationToken);

        buffer.RecordOutput(
            FirstGeneration,
            1,
            ProcessOutputStream.Stdout,
            new byte[] { (byte)'x' },
            EventTime);
        buffer.RequestTermination(ExtensionServiceLogTerminationReason.ServiceRemoved);
        Assert.False(sink.Completed.Task.IsCompleted);
        buffer.RecordProcessExited(FirstGeneration, 1, 0, EventTime);

        await sink.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscription.DeliveryTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscription.DisposeAsync();

        var entries = sink.Snapshot();
        Assert.Equal(
            new[]
            {
                ExtensionServiceLogEntryKind.CurrentState,
                ExtensionServiceLogEntryKind.GenerationStarted,
                ExtensionServiceLogEntryKind.Output,
                ExtensionServiceLogEntryKind.ProcessExited,
                ExtensionServiceLogEntryKind.Termination
            },
            entries.Select(static entry => entry.Kind).ToArray());
        Assert.Equal(
            new long?[] { 1, 2, 3, 4 },
            entries.Skip(1).Select(static entry => entry.Sequence).ToArray());
        Assert.Equal(ExtensionServiceLogTerminationReason.ServiceRemoved, entries[^1].TerminationReason);
        Assert.Equal(1, sink.CompletionCount);
        Assert.Equal(1, detachedCount);
        Assert.Equal(1, quiescedCount);
    }


    [Fact]
    public async Task RegistryDisposeCompletesSubscriptionsDespiteActiveGenerations()
    {
        using var registry = new HostServiceLogBufferRegistry(
            new HostRuntimeOptions("Host=unit-test", "test-node", readOnly: false),
            executor: null);
        var buffer = registry.GetOrCreate(ServiceId);
        registry.OnGenerationStarted(ServiceId, FirstGeneration, 1, EventTime);
        var sink = new RecordingSink();
        await using var subscription = Subscribe(buffer, sink);
        subscription.Start();
        await sink.WaitForEntryCountAsync(2, TestContext.Current.CancellationToken);

        registry.Dispose();

        await sink.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscription.DeliveryTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, buffer.ActiveGenerationCount);
        Assert.Equal(ExtensionServiceLogTerminationReason.HostShutdown, sink.Snapshot()[^1].TerminationReason);
        Assert.Equal(1, sink.CompletionCount);
    }

    [Fact]
    public async Task RemovedServiceClearsRetainedEntriesAfterDrainWithoutResettingSequence()
    {
        var buffer = new HostServiceLogBuffer(ServiceId, 1_048_576);
        buffer.RecordOutput(FirstGeneration, 1, ProcessOutputStream.Stdout, new byte[] { 1 }, EventTime);
        var removalSink = new RecordingSink();
        await using var removalSubscription = Subscribe(buffer, removalSink);
        removalSubscription.Start();
        await removalSink.WaitForEntryCountAsync(2, TestContext.Current.CancellationToken);

        buffer.RequestTermination(ExtensionServiceLogTerminationReason.ServiceRemoved);

        await removalSink.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await removalSubscription.DeliveryTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var lateSink = new RecordingSink();
        await using var lateSubscription = Subscribe(buffer, lateSink);
        lateSubscription.Start();
        await lateSink.WaitForEntryCountAsync(3, TestContext.Current.CancellationToken);

        var entries = lateSink.Snapshot();
        Assert.Equal(
            new[]
            {
                ExtensionServiceLogEntryKind.CurrentState,
                ExtensionServiceLogEntryKind.Gap,
                ExtensionServiceLogEntryKind.Termination
            },
            entries.Select(static entry => entry.Kind).ToArray());
        Assert.Equal(1L, entries[1].FirstMissingSequence);
        Assert.Equal(2L, entries[1].LastMissingSequence);
        Assert.Equal(ExtensionServiceLogTerminationReason.ServiceRemoved, entries[^1].TerminationReason);
    }

    [Fact]
    public async Task GenerationStartReopensBufferBeforeRecordingGeneration()
    {
        using var registry = new HostServiceLogBufferRegistry(
            new HostRuntimeOptions("Host=unit-test", "test-node", readOnly: false),
            executor: null);
        registry.SynchronizeConfiguration(
            CreateConfiguration(version: 1, serviceEnabled: true),
            new HashSet<Guid> { ServiceId });
        var buffer = registry.GetOrCreate(ServiceId);
        buffer.RequestTermination(ExtensionServiceLogTerminationReason.ServiceDisabled);

        registry.OnGenerationStarted(ServiceId, FirstGeneration, 1, EventTime);

        Assert.Equal(1, buffer.ActiveGenerationCount);
        var sink = new RecordingSink();
        await using var subscription = Subscribe(buffer, sink);
        subscription.Start();
        await sink.WaitForEntryCountAsync(3, TestContext.Current.CancellationToken);
        registry.OnGenerationExited(ServiceId, FirstGeneration, 1, 0, EventTime);
        await sink.WaitForEntryCountAsync(4, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[]
            {
                ExtensionServiceLogEntryKind.CurrentState,
                ExtensionServiceLogEntryKind.Gap,
                ExtensionServiceLogEntryKind.GenerationStarted,
                ExtensionServiceLogEntryKind.ProcessExited
            },
            sink.Snapshot().Select(static entry => entry.Kind).ToArray());
        Assert.Equal(0, buffer.ActiveGenerationCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingHostShutdownCannotBeCancelledOrOverwritten(bool cancelPendingTermination)
    {
        var buffer = new HostServiceLogBuffer(ServiceId, 1_048_576);
        buffer.RecordGenerationStarted(FirstGeneration, 1, EventTime);
        var sink = new RecordingSink();
        await using var subscription = Subscribe(buffer, sink);
        subscription.Start();
        await sink.WaitForEntryCountAsync(2, TestContext.Current.CancellationToken);

        buffer.RequestTermination(ExtensionServiceLogTerminationReason.HostShutdown);
        if (cancelPendingTermination)
        {
            buffer.CancelPendingTermination();
        }
        else
        {
            buffer.RequestTermination(ExtensionServiceLogTerminationReason.ServiceDisabled);
        }

        buffer.RecordProcessExited(FirstGeneration, 1, 0, EventTime);

        await sink.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionServiceLogTerminationReason.HostShutdown, sink.Snapshot()[^1].TerminationReason);
    }

    [Fact]
    public async Task SlowSinkQueueOverflowReportsDroppedSequencesBeforeNextEntry()
    {
        var buffer = new HostServiceLogBuffer(ServiceId, HostServiceLogBuffer.MaximumDeliveryEntryBytes);
        var callbackEntered = NewSignal();
        var releaseCallback = NewSignal();
        var sink = new RecordingSink(entry =>
        {
            if (entry.Kind == ExtensionServiceLogEntryKind.CurrentState)
            {
                callbackEntered.TrySetResult(null);
                releaseCallback.Task.GetAwaiter().GetResult();
            }
        });
        await using var subscription = Subscribe(buffer, sink);
        subscription.Start();

        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            buffer.RecordOutput(FirstGeneration, 1, ProcessOutputStream.Stdout, new byte[] { 1 }, EventTime);
            buffer.RecordOutput(FirstGeneration, 1, ProcessOutputStream.Stdout, new byte[] { 2 }, EventTime);
            buffer.RecordOutput(FirstGeneration, 1, ProcessOutputStream.Stdout, new byte[] { 3 }, EventTime);
        }
        finally
        {
            releaseCallback.TrySetResult(null);
        }

        await sink.WaitForEntryCountAsync(3, TestContext.Current.CancellationToken);
        var entries = sink.Snapshot();
        Assert.Equal(ExtensionServiceLogEntryKind.CurrentState, entries[0].Kind);
        Assert.Equal(ExtensionServiceLogEntryKind.Gap, entries[1].Kind);
        Assert.Equal(1L, entries[1].FirstMissingSequence);
        Assert.Equal(2L, entries[1].LastMissingSequence);
        Assert.Equal(ExtensionServiceLogEntryKind.Output, entries[2].Kind);
        Assert.Equal(3L, entries[2].Sequence);
    }

    [Fact]
    public async Task SubscriptionWithoutCursorBeginsWithCurrentStateMarker()
    {
        var buffer = new HostServiceLogBuffer(ServiceId, 1_048_576);
        buffer.RecordOutput(
            FirstGeneration,
            1,
            ProcessOutputStream.Stdout,
            new byte[] { (byte)'r' },
            EventTime);
        var sink = new RecordingSink();
        await using var subscription = Subscribe(
            buffer,
            sink,
            currentState: ExtensionServiceLifecycleState.Running);
        subscription.Start();
        await sink.WaitForEntryCountAsync(2, TestContext.Current.CancellationToken);

        var entries = sink.Snapshot();
        Assert.Equal(ExtensionServiceLogEntryKind.CurrentState, entries[0].Kind);
        Assert.Null(entries[0].Sequence);
        Assert.Equal(ExtensionServiceLifecycleState.Running, entries[0].LifecycleState);
        Assert.Equal(ExtensionServiceLogEntryKind.Output, entries[1].Kind);
        Assert.Equal(1L, entries[1].Sequence);
    }

    private static HostConfigurationSnapshot CreateConfiguration(long version, bool serviceEnabled) =>
        new(
            version,
            new GlobalSettingsConfiguration(version: version),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray.Create(new ServiceConfiguration(
                ServiceId,
                enabled: serviceEnabled,
                fileName: "/bin/sh",
                argumentList: ImmutableArray<string>.Empty,
                workingDirectory: "/tmp",
                environment: ImmutableDictionary<string, string>.Empty,
                startMode: ServiceStartMode.Lazy,
                restartPolicy: Nekolla.Nekostick.Contracts.ServiceRestartPolicy.Never,
                healthCheck: new ServiceHealthCheckConfiguration(
                    ServiceHealthCheckType.Process,
                    httpPath: null,
                    timeout: TimeSpan.FromSeconds(1)),
                createdAt: EventTime,
                updatedAt: EventTime,
                version: version)),
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

    private static HostServiceLogSubscription Subscribe(
        HostServiceLogBuffer buffer,
        RecordingSink sink,
        long? sinceSequence = null,
        ExtensionServiceLifecycleState currentState = ExtensionServiceLifecycleState.Unknown,
        Action<HostServiceLogSubscription>? onDetached = null,
        Action<HostServiceLogSubscription>? onQuiesced = null)
    {
        var code = buffer.TrySubscribe(
            sinceSequence,
            currentState,
            sink,
            onDetached ?? IgnoreSubscription,
            onQuiesced ?? IgnoreSubscription,
            out var subscription);
        Assert.Equal(ExtensionServiceLogCode.Subscribed, code);
        Assert.NotNull(subscription);
        return subscription!;
    }

    private static byte[] CreateChunk(byte value, int length)
    {
        var chunk = new byte[length];
        Array.Fill(chunk, value);
        return chunk;
    }

    private static void IgnoreSubscription(HostServiceLogSubscription subscription) { }

    private static TaskCompletionSource<object?> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordingSink : IExtensionServiceLogSink
    {
        private readonly object _gate = new();
        private readonly Action<ExtensionServiceLogEntry>? _onEntry;
        private readonly List<ExtensionServiceLogEntry> _entries = [];
        private TaskCompletionSource<object?> _entrySignal = NewSignal();
        private int _completionCount;

        internal RecordingSink(Action<ExtensionServiceLogEntry>? onEntry = null) => _onEntry = onEntry;

        internal TaskCompletionSource<object?> Completed { get; } = NewSignal();

        internal int CompletionCount => Volatile.Read(ref _completionCount);

        public void OnEntry(ExtensionServiceLogEntry entry)
        {
            _onEntry?.Invoke(entry);
            TaskCompletionSource<object?> signal;
            lock (_gate)
            {
                _entries.Add(entry);
                signal = _entrySignal;
                _entrySignal = NewSignal();
            }

            signal.TrySetResult(null);
        }

        public void OnCompleted()
        {
            Interlocked.Increment(ref _completionCount);
            Completed.TrySetResult(null);
        }

        internal ExtensionServiceLogEntry[] Snapshot()
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }

        internal async Task WaitForEntryCountAsync(int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (_entries.Count >= count)
                    {
                        return;
                    }

                    signal = _entrySignal.Task;
                }

                await signal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }
}
