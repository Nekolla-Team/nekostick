using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionServiceLogFacadeTests
{
    private static readonly Guid ServiceId = new("0198a1af-6e94-7b25-9732-59c9075b14f6");
    private static readonly Guid InvalidServiceId = new("0198a1af-6e94-4b25-9732-59c9075b14f6");

    [Fact]
    public async Task InvalidAndUnconfiguredServiceIdsReturnSafeCodes()
    {
        await using var harness = CreateHarness(configured: false);
        var sink = new RecordingSink();

        var invalid = await harness.Facade.SubscribeAsync(
            InvalidServiceId,
            sink,
            cancellationToken: TestContext.Current.CancellationToken);
        var missing = await harness.Facade.SubscribeAsync(
            ServiceId,
            sink,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(invalid.Succeeded);
        Assert.Equal(ExtensionServiceLogCode.InvalidArgument, invalid.Code);
        Assert.Null(invalid.Subscription);
        Assert.False(missing.Succeeded);
        Assert.Equal(ExtensionServiceLogCode.NotFound, missing.Code);
        Assert.Null(missing.Subscription);
    }

    [Fact]
    public async Task ConfiguredServiceWithoutBufferRegistryReturnsUnsupported()
    {
        await using var harness = CreateHarness(includeBufferRegistry: false);
        var result = await harness.Facade.SubscribeAsync(
            ServiceId,
            new RecordingSink(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionServiceLogCode.Unsupported, result.Code);
        Assert.Null(result.Subscription);
    }

    [Fact]
    public async Task CursorBeyondLatestSequenceReturnsInvalidCursor()
    {
        await using var harness = CreateHarness();
        var result = await harness.Facade.SubscribeAsync(
            ServiceId,
            new RecordingSink(),
            sinceSequence: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionServiceLogCode.InvalidCursor, result.Code);
        Assert.Null(result.Subscription);
    }

    [Fact]
    public async Task ForeignConfiguredServiceCanBeSubscribed()
    {
        await using var harness = CreateHarness(serviceOwnerExtensionId: "another.extension");
        var sink = new RecordingSink();
        var result = await harness.Facade.SubscribeAsync(
            ServiceId,
            sink,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(ExtensionServiceLogCode.Subscribed, result.Code);
        Assert.NotNull(result.Subscription);
        await sink.WaitForEntryCountAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionServiceLogEntryKind.CurrentState, sink.Snapshot()[0].Kind);
        await result.Subscription!.DisposeAsync();
    }

    [Fact]
    public async Task ExtensionUnloadTerminatesSubscriptionAndAwaitsCallbackQuiescence()
    {
        await using var harness = CreateHarness();
        var callbackEntered = NewSignal();
        var releaseCallback = NewSignal();
        ExtensionLifecycleOperationResult? lifecycleResult = null;
        var lifecycle = new ExtensionLifecycleApi(
            () => null,
            _ => ValueTask.FromResult(new ExtensionLifecycleOperationResult(
                true,
                ExtensionLifecycleOperationCode.Accepted,
                null)),
            _ => ValueTask.FromResult(new ExtensionLifecycleOperationResult(
                true,
                ExtensionLifecycleOperationCode.Accepted,
                null)));
        var sink = new RecordingSink(entry =>
        {
            if (entry.Kind != ExtensionServiceLogEntryKind.CurrentState)
            {
                return;
            }

            lifecycleResult = lifecycle.RequestUnloadAsync().AsTask().GetAwaiter().GetResult();
            callbackEntered.TrySetResult(null);
            releaseCallback.Task.GetAwaiter().GetResult();
        });
        var result = await harness.Facade.SubscribeAsync(
            ServiceId,
            sink,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Subscription);

        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotNull(lifecycleResult);
            Assert.Equal(ExtensionLifecycleOperationCode.Reentrant, lifecycleResult!.Code);

            var disposeTask = harness.Facade.DisposeAsync().AsTask();
            Assert.False(disposeTask.IsCompleted);
            releaseCallback.TrySetResult(null);
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await result.Subscription!.DisposeAsync();

            var entries = sink.Snapshot();
            Assert.Equal(
                new[]
                {
                    ExtensionServiceLogEntryKind.CurrentState,
                    ExtensionServiceLogEntryKind.Termination
                },
                entries.Select(static entry => entry.Kind).ToArray());
            Assert.Null(entries[1].Sequence);
            Assert.Equal(ExtensionServiceLogTerminationReason.ExtensionUnloaded, entries[1].TerminationReason);
            Assert.Equal(1, sink.CompletionCount);
        }
        finally
        {
            releaseCallback.TrySetResult(null);
        }
    }


    [Fact]
    public async Task DetachAllCancelsQueuedCallbacksAfterUnload()
    {
        await using var harness = CreateHarness();
        var callbackEntered = NewSignal();
        var releaseCallback = NewSignal();
        var sink = new RecordingSink(entry =>
        {
            if (entry.Kind != ExtensionServiceLogEntryKind.CurrentState)
            {
                return;
            }

            callbackEntered.TrySetResult(null);
            releaseCallback.Task.GetAwaiter().GetResult();
        });
        var result = await harness.Facade.SubscribeAsync(
            ServiceId,
            sink,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        var subscription = Assert.IsType<HostServiceLogSubscription>(result.Subscription);

        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            harness.Registry!.GetOrCreate(ServiceId).RecordStartupFailure(
                attemptNumber: 1,
                instanceId: null,
                failureStage: ExtensionServiceFailureStage.Spawn,
                failureCode: ExtensionServiceFailureCode.ExecutableMissing,
                failureReason: "test failure",
                failedAt: DateTimeOffset.UnixEpoch);
            harness.Facade.DetachAll();
            releaseCallback.TrySetResult(null);
            await subscription.DeliveryTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(
                new[] { ExtensionServiceLogEntryKind.CurrentState },
                sink.Snapshot().Select(static entry => entry.Kind).ToArray());
            Assert.Equal(0, sink.CompletionCount);
        }
        finally
        {
            releaseCallback.TrySetResult(null);
        }
    }

    private static FacadeHarness CreateHarness(
        bool configured = true,
        bool includeBufferRegistry = true,
        string? serviceOwnerExtensionId = "another.extension")
    {
        var services = configured
            ? ImmutableArray.Create(CreateServiceConfiguration())
            : ImmutableArray<ServiceConfiguration>.Empty;
        var snapshot = new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            ImmutableArray<RouteConfiguration>.Empty,
            services,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
        var holder = new HostConfigurationSnapshotHolder();
        var serviceOwners = configured
            ? ImmutableDictionary<Guid, string?>.Empty.Add(ServiceId, serviceOwnerExtensionId)
            : ImmutableDictionary<Guid, string?>.Empty;
        Assert.Equal(
            SnapshotAdmission.Accepted,
            holder.TryReplace(snapshot, dispatchGeneration: null, serviceOwners: serviceOwners));
        var runtimeState = new HostRuntimeState(holder, new HostNodeOptions(false, false, false));
        HostServiceLogBufferRegistry? registry = includeBufferRegistry
            ? new HostServiceLogBufferRegistry(
                new HostRuntimeOptions("Host=unit-test", "test-node", readOnly: false),
                executor: null)
            : null;
        var facade = new ExtensionServiceLogFacade(
            "fixture.extension.deterministic",
            runtimeState,
            registry,
            runtimeAccessor: null,
            serviceOutput: new ExtensionServiceOutputFacade(
                "fixture.extension.deterministic",
                runtimeState,
                executor: null,
                logger: NullLogger.Instance),
            logger: NullLogger.Instance);
        return new FacadeHarness(facade, registry);
    }

    private static ServiceConfiguration CreateServiceConfiguration() =>
        new(
            ServiceId,
            enabled: true,
            fileName: "/bin/sh",
            argumentList: ImmutableArray<string>.Empty,
            workingDirectory: "/tmp",
            environment: ImmutableDictionary<string, string>.Empty,
            startMode: ServiceStartMode.Lazy,
            restartPolicy: ContractRestartPolicy.Never,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch,
            version: 1);

    private static TaskCompletionSource<object?> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FacadeHarness : IAsyncDisposable
    {
        private readonly HostServiceLogBufferRegistry? _registry;

        internal FacadeHarness(ExtensionServiceLogFacade facade, HostServiceLogBufferRegistry? registry)
        {
            Facade = facade;
            _registry = registry;
        }

        internal ExtensionServiceLogFacade Facade { get; }
        internal HostServiceLogBufferRegistry? Registry => _registry;

        public async ValueTask DisposeAsync()
        {
            await Facade.DisposeAsync();
            if (_registry is not null)
            {
                await _registry.DisposeAsync();
            }
        }
    }

    private sealed class RecordingSink : IExtensionServiceLogSink
    {
        private readonly object _gate = new();
        private readonly Action<ExtensionServiceLogEntry>? _onEntry;
        private readonly List<ExtensionServiceLogEntry> _entries = [];
        private TaskCompletionSource<object?> _entrySignal = NewSignal();
        private int _completionCount;

        internal RecordingSink(Action<ExtensionServiceLogEntry>? onEntry = null) => _onEntry = onEntry;


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
