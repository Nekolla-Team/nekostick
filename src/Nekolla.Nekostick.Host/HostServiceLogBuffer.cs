using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Supervision;

namespace Nekolla.Nekostick.Host;

internal sealed class HostServiceLogBufferRegistry : IProcessOutputTap, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, HostServiceLogBuffer> _buffers = new();
    private readonly object _configurationGate = new();
    private readonly int _byteBudget;
    private IDisposable? _outputTapSubscription;
    private IReadOnlyDictionary<Guid, bool>? _configuredServices;
    private long _configurationVersion = -1;
    private int _disposed;

    internal HostServiceLogBufferRegistry(
        HostRuntimeOptions options,
        PosixProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(options);
        _byteBudget = options.ServiceLogBufferByteBudget;
        _outputTapSubscription = executor?.SubscribeOutputTap(this);
    }
    internal bool HasOutputTap => _outputTapSubscription is not null;

    internal HostServiceLogBuffer GetOrCreate(Guid serviceId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var buffer = _buffers.GetOrAdd(serviceId, id => new HostServiceLogBuffer(id, _byteBudget));
        ApplyConfiguration(buffer, serviceId);
        return buffer;
    }

    internal void SynchronizeConfiguration(
        HostConfigurationSnapshot configuration,
        IReadOnlySet<Guid> enabledServices)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(enabledServices);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_configurationGate)
        {
            if (_configurationVersion == configuration.Version)
            {
                return;
            }

            var configuredServices = configuration.Services.ToDictionary(
                static service => service.Id,
                service => service.Enabled && enabledServices.Contains(service.Id));
            _configuredServices = configuredServices;
            _configurationVersion = configuration.Version;
            foreach (var pair in _buffers)
            {
                ApplyConfigurationLocked(pair.Value, pair.Key);
            }
        }
    }

    public void OnGenerationExited(
        Guid serviceId,
        ProcessInstanceId instanceId,
        int attemptNumber,
        int? exitCode,
        DateTimeOffset exitedAt)
    {
        if (_buffers.TryGetValue(serviceId, out var buffer))
        {
            buffer.RecordProcessExited(instanceId, attemptNumber, exitCode, exitedAt);
        }

        TryDisposeTapWhenIdle();
    }

    internal void RecordStartupFailure(
        Guid serviceId,
        int attemptNumber,
        ProcessInstanceId? instanceId,
        ExtensionServiceFailureStage failureStage,
        ExtensionServiceFailureCode failureCode,
        string? failureReason,
        DateTimeOffset failedAt)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            GetOrCreate(serviceId).RecordStartupFailure(
                attemptNumber,
                instanceId,
                failureStage,
                failureCode,
                failureReason,
                failedAt);
        }

    }

    internal void TerminateAll(ExtensionServiceLogTerminationReason reason)
    {
        foreach (var buffer in _buffers.Values)
        {
            buffer.RequestTermination(reason);
        }
    }

    public void OnGenerationStarted(
        Guid serviceId,
        ProcessInstanceId instanceId,
        int attemptNumber,
        DateTimeOffset startedAt)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_configurationGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var buffer = _buffers.GetOrAdd(serviceId, id => new HostServiceLogBuffer(id, _byteBudget));
            ApplyConfigurationLocked(buffer, serviceId);
            buffer.RecordGenerationStarted(instanceId, attemptNumber, startedAt);
        }
    }

    public void OnOutputChunk(
        Guid serviceId,
        ProcessInstanceId instanceId,
        int attemptNumber,
        ProcessOutputStream stream,
        ReadOnlyMemory<byte> chunk,
        DateTimeOffset capturedAt)
    {
        if (_buffers.TryGetValue(serviceId, out var buffer))
        {
            buffer.RecordOutput(instanceId, attemptNumber, stream, chunk, capturedAt);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            foreach (var buffer in _buffers.Values)
            {
                buffer.ForceTerminate(ExtensionServiceLogTerminationReason.HostShutdown);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _outputTapSubscription, null)?.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ApplyConfiguration(HostServiceLogBuffer buffer, Guid serviceId)
    {
        lock (_configurationGate)
        {
            ApplyConfigurationLocked(buffer, serviceId);
        }
    }

    private void ApplyConfigurationLocked(HostServiceLogBuffer buffer, Guid serviceId)
    {
        if (_configuredServices is not { } configuredServices)
        {
            return;
        }

        if (!configuredServices.TryGetValue(serviceId, out var enabled))
        {
            buffer.RequestTermination(ExtensionServiceLogTerminationReason.ServiceRemoved);
        }
        else if (enabled)
        {
            buffer.CancelPendingTermination();
            buffer.Reopen();
        }
        else
        {
            buffer.RequestTermination(ExtensionServiceLogTerminationReason.ServiceDisabled);
        }
    }

    private void TryDisposeTapWhenIdle()
    {
        if (Volatile.Read(ref _disposed) == 0 || _buffers.Values.Any(static buffer => buffer.ActiveGenerationCount != 0))
        {
            return;
        }

        Interlocked.Exchange(ref _outputTapSubscription, null)?.Dispose();
    }
}

internal sealed class HostServiceLogBuffer
{
    private const int EntryMetadataBytes = 128;
    private const int MaximumFailureReasonBytes = 512 * sizeof(char);
    private const int MaximumProcessOutputChunkBytes = 8 * 1024;
    internal const int MaximumDeliveryEntryBytes = EntryMetadataBytes + MaximumFailureReasonBytes + MaximumProcessOutputChunkBytes;

    private readonly object _gate = new();
    private readonly Guid _serviceId;
    private readonly int _byteBudget;
    private readonly Queue<ExtensionServiceLogEntry> _entries = new();
    private readonly HashSet<StartupFailureKey> _retainedStartupFailures = [];
    private readonly HashSet<HostServiceLogSubscription> _subscriptions = [];
    private readonly HashSet<Guid> _activeGenerations = [];
    private int _retainedBytes;
    private long _lastSequence;
    private bool _terminated;
    private ExtensionServiceLogTerminationReason? _terminationReason;
    private ExtensionServiceLogTerminationReason? _pendingTerminationReason;
    private ExtensionServiceLogEntry? _terminationEntry;

    internal HostServiceLogBuffer(Guid serviceId, int byteBudget)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(byteBudget, 0);
        _serviceId = serviceId;
        _byteBudget = byteBudget;
    }
    internal int ActiveGenerationCount
    {
        get
        {
            lock (_gate)
            {
                return _activeGenerations.Count;
            }
        }
    }

    internal ExtensionServiceLogCode TrySubscribe(
        long? sinceSequence,
        ExtensionServiceLifecycleState currentState,
        IExtensionServiceLogSink sink,
        Action<HostServiceLogSubscription> onDetached,
        Action<HostServiceLogSubscription> onQuiesced,
        out HostServiceLogSubscription? subscription)
    {
        subscription = null;
        if (sinceSequence is < 0)
        {
            return ExtensionServiceLogCode.InvalidArgument;
        }

        lock (_gate)
        {
            if (sinceSequence is { } cursor && cursor > _lastSequence)
            {
                return ExtensionServiceLogCode.InvalidCursor;
            }

            var initialState = new ExtensionServiceLogEntry(
                ExtensionServiceLogEntryKind.CurrentState,
                _serviceId,
                sequence: null,
                DateTimeOffset.UtcNow,
                lifecycleState: currentState);
            var replayEntries = new List<ExtensionServiceLogEntry>(_entries.Count);
            var replayCursor = sinceSequence ?? 0;
            var terminationIncluded = false;
            foreach (var entry in _entries)
            {
                if (entry.Sequence is not { } sequence || sequence <= replayCursor)
                {
                    continue;
                }

                if (sequence > replayCursor + 1)
                {
                    replayEntries.Add(CreateGap(replayCursor + 1, sequence - 1));
                }

                replayEntries.Add(entry);
                replayCursor = sequence;
                terminationIncluded |= ReferenceEquals(entry, _terminationEntry);
            }

            if (replayCursor < _lastSequence)
            {
                replayEntries.Add(CreateGap(replayCursor + 1, _lastSequence));
            }

            if (_terminated && !terminationIncluded)
            {
                replayEntries.Add(CreateTerminationEntry(_terminationReason!.Value, sequence: null));
            }

            var created = new HostServiceLogSubscription(
                this,
                sink,
                initialState,
                sinceSequence ?? 0,
                replayEntries,
                _byteBudget,
                onDetached,
                onQuiesced);
            _subscriptions.Add(created);
            subscription = created;

            if (_terminated)
            {
                created.CompleteNormally();
            }

            return ExtensionServiceLogCode.Subscribed;
        }
    }

    internal void RecordGenerationStarted(
        ProcessInstanceId instanceId,
        int attemptNumber,
        DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            if (_terminated)
            {
                return;
            }
            _activeGenerations.Add(instanceId.Value);

            var entry = new ExtensionServiceLogEntry(
                ExtensionServiceLogEntryKind.GenerationStarted,
                _serviceId,
                NextSequenceLocked(),
                startedAt,
                processInstanceId: instanceId.Value,
                attemptNumber: attemptNumber);
            AppendLocked(entry, null);
        }
    }

    internal void RecordOutput(
        ProcessInstanceId instanceId,
        int attemptNumber,
        ProcessOutputStream stream,
        ReadOnlyMemory<byte> chunk,
        DateTimeOffset capturedAt)
    {
        var outputStream = stream switch
        {
            ProcessOutputStream.Stdout => ExtensionServiceOutputStream.Stdout,
            ProcessOutputStream.Stderr => ExtensionServiceOutputStream.Stderr,
            _ => throw new ArgumentOutOfRangeException(nameof(stream))
        };
        lock (_gate)
        {
            if (_terminated)
            {
                return;
            }
            var immutableData = ImmutableCollectionsMarshal.AsImmutableArray(chunk.ToArray());

            var entry = new ExtensionServiceLogEntry(
                ExtensionServiceLogEntryKind.Output,
                _serviceId,
                NextSequenceLocked(),
                capturedAt,
                outputStream,
                immutableData,
                instanceId.Value,
                attemptNumber);
            AppendLocked(entry, null);
        }
    }

    internal void RecordProcessExited(
        ProcessInstanceId instanceId,
        int attemptNumber,
        int? exitCode,
        DateTimeOffset exitedAt)
    {
        lock (_gate)
        {
            if (_terminated || !_activeGenerations.Remove(instanceId.Value))
            {
                return;
            }

            var entry = new ExtensionServiceLogEntry(
                ExtensionServiceLogEntryKind.ProcessExited,
                _serviceId,
                NextSequenceLocked(),
                exitedAt,
                processInstanceId: instanceId.Value,
                attemptNumber: attemptNumber,
                processExitCode: exitCode);
            AppendLocked(entry, null);
            if (_activeGenerations.Count == 0 && _pendingTerminationReason is { } reason)
            {
                FinalizeTerminationLocked(reason);
            }
        }
    }

    internal void RecordStartupFailure(
        int attemptNumber,
        ProcessInstanceId? instanceId,
        ExtensionServiceFailureStage failureStage,
        ExtensionServiceFailureCode failureCode,
        string? failureReason,
        DateTimeOffset failedAt)
    {
        if (failureStage == ExtensionServiceFailureStage.None || failureCode == ExtensionServiceFailureCode.None)
        {
            return;
        }

        var boundedReason = failureReason is { Length: > 512 }
            ? failureReason[..512]
            : failureReason;
        var key = new StartupFailureKey(attemptNumber, failureStage, failureCode, boundedReason);
        lock (_gate)
        {
            if (_terminated || _retainedStartupFailures.Contains(key))
            {
                return;
            }

            var entry = new ExtensionServiceLogEntry(
                ExtensionServiceLogEntryKind.StartupFailed,
                _serviceId,
                NextSequenceLocked(),
                failedAt,
                processInstanceId: instanceId?.Value,
                attemptNumber: attemptNumber,
                failureStage: failureStage,
                failureCode: failureCode,
                failureReason: boundedReason);
            AppendLocked(entry, key);
        }
    }

    internal void ForceTerminate(ExtensionServiceLogTerminationReason reason)
    {
        lock (_gate)
        {
            _activeGenerations.Clear();
            FinalizeTerminationLocked(reason);
        }
    }

    internal void RequestTermination(ExtensionServiceLogTerminationReason reason)
    {
        lock (_gate)
        {
            if (_terminated || _pendingTerminationReason == ExtensionServiceLogTerminationReason.HostShutdown)
            {
                return;
            }

            _pendingTerminationReason = reason;
            if (_activeGenerations.Count == 0)
            {
                FinalizeTerminationLocked(reason);
            }
        }
    }

    internal void CancelPendingTermination()
    {
        lock (_gate)
        {
            if (_terminationReason != ExtensionServiceLogTerminationReason.HostShutdown &&
                _pendingTerminationReason != ExtensionServiceLogTerminationReason.HostShutdown)
            {
                _pendingTerminationReason = null;
            }
        }
    }

    private void FinalizeTerminationLocked(ExtensionServiceLogTerminationReason reason)
    {
        if (_terminated)
        {
            return;
        }

        _pendingTerminationReason = null;
        _terminated = true;
        _terminationReason = reason;
        var entry = CreateTerminationEntry(reason, NextSequenceLocked());
        _terminationEntry = entry;
        AppendLocked(entry, null);
        foreach (var subscription in _subscriptions)
        {
            subscription.CompleteNormally();
        }
        if (reason == ExtensionServiceLogTerminationReason.ServiceRemoved && _subscriptions.Count == 0)
        {
            ClearRemovedServiceEntriesLocked();
        }
    }

    private void ClearRemovedServiceEntriesLocked()
    {
        _entries.Clear();
        _retainedStartupFailures.Clear();
        _retainedBytes = 0;
        // Keep _lastSequence so late subscribers observe the missing range rather than a reset cursor.
    }

    internal void Reopen()
    {
        lock (_gate)
        {
            if (!_terminated || _terminationReason == ExtensionServiceLogTerminationReason.HostShutdown)
            {
                return;
            }

            _entries.Clear();
            _retainedStartupFailures.Clear();
            _retainedBytes = 0;
            _terminationReason = null;
            _pendingTerminationReason = null;
            _terminationEntry = null;
            _terminated = false;
        }
    }

    internal void RemoveSubscription(HostServiceLogSubscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.Remove(subscription) &&
                _subscriptions.Count == 0 &&
                _terminated &&
                _terminationReason == ExtensionServiceLogTerminationReason.ServiceRemoved)
            {
                ClearRemovedServiceEntriesLocked();
            }
        }
    }

    internal void TerminateSubscription(
        HostServiceLogSubscription subscription,
        ExtensionServiceLogTerminationReason reason)
    {
        lock (_gate)
        {
            if (!_subscriptions.Remove(subscription))
            {
                return;
            }
        }

        subscription.Enqueue(CreateTerminationEntry(reason, sequence: null));
        subscription.CompleteNormally();
    }

    private void AppendLocked(ExtensionServiceLogEntry entry, StartupFailureKey? failureKey)
    {
        var entryBytes = EstimateEntryBytes(entry);
        if (entryBytes <= _byteBudget)
        {
            while (_retainedBytes > _byteBudget - entryBytes && _entries.TryDequeue(out var oldest))
            {
                _retainedBytes -= EstimateEntryBytes(oldest);
                if (oldest.Kind == ExtensionServiceLogEntryKind.StartupFailed &&
                    oldest.AttemptNumber is { } attemptNumber)
                {
                    _retainedStartupFailures.Remove(new StartupFailureKey(
                        attemptNumber,
                        oldest.FailureStage,
                        oldest.FailureCode,
                        oldest.FailureReason));
                }
            }

            _entries.Enqueue(entry);
            _retainedBytes += entryBytes;
            if (failureKey is { } key)
            {
                _retainedStartupFailures.Add(key);
            }
        }

        foreach (var subscription in _subscriptions)
        {
            subscription.Enqueue(entry);
        }
    }

    private long NextSequenceLocked() => _lastSequence = checked(_lastSequence + 1);

    private ExtensionServiceLogEntry CreateGap(long firstMissingSequence, long lastMissingSequence) =>
        new(
            ExtensionServiceLogEntryKind.Gap,
            _serviceId,
            lastMissingSequence,
            DateTimeOffset.UtcNow,
            firstMissingSequence: firstMissingSequence,
            lastMissingSequence: lastMissingSequence);

    private ExtensionServiceLogEntry CreateTerminationEntry(
        ExtensionServiceLogTerminationReason reason,
        long? sequence) =>
        new(
            ExtensionServiceLogEntryKind.Termination,
            _serviceId,
            sequence,
            DateTimeOffset.UtcNow,
            terminationReason: reason);

    private static int EstimateEntryBytes(ExtensionServiceLogEntry entry) =>
        EntryMetadataBytes + entry.Data.Length + (entry.FailureReason?.Length * sizeof(char) ?? 0);

    private readonly record struct StartupFailureKey(
        int AttemptNumber,
        ExtensionServiceFailureStage FailureStage,
        ExtensionServiceFailureCode FailureCode,
        string? FailureReason);
}

internal sealed class HostServiceLogSubscription : IExtensionServiceLogSubscription
{
    private readonly HostServiceLogBuffer _owner;
    private readonly IExtensionServiceLogSink _sink;
    private readonly ExtensionServiceLogEntry _initialState;
    private readonly IReadOnlyList<ExtensionServiceLogEntry> _replayEntries;
    private readonly Channel<ExtensionServiceLogEntry> _channel;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly Action<HostServiceLogSubscription> _onDetached;
    private readonly Action<HostServiceLogSubscription> _onQuiesced;
    private readonly object _startGate = new();
    private Task? _deliveryTask;
    private long _lastDeliveredSequence;
    private long _highestSequenceQueued;
    private int _disposed;
    private int _completed;
    private int _detached;
    private int _quiesced;

    internal HostServiceLogSubscription(
        HostServiceLogBuffer owner,
        IExtensionServiceLogSink sink,
        ExtensionServiceLogEntry initialState,
        long sinceSequence,
        IReadOnlyList<ExtensionServiceLogEntry> replayEntries,
        int byteBudget,
        Action<HostServiceLogSubscription> onDetached,
        Action<HostServiceLogSubscription> onQuiesced)
    {
        _owner = owner;
        _sink = sink;
        _initialState = initialState;
        _replayEntries = replayEntries;
        _lastDeliveredSequence = sinceSequence;
        _onDetached = onDetached;
        _onQuiesced = onQuiesced;
        _channel = Channel.CreateBounded<ExtensionServiceLogEntry>(new BoundedChannelOptions(
            Math.Max(1, byteBudget / HostServiceLogBuffer.MaximumDeliveryEntryBytes))
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    internal void Start()
    {
        lock (_startGate)
        {
            if (_deliveryTask is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _deliveryTask = Task.Run(DeliverAsync);
        }
    }

    internal Task DeliveryTask
    {
        get
        {
            lock (_startGate)
            {
                return _deliveryTask ?? Task.CompletedTask;
            }
        }
    }

    internal Guid ServiceId => _initialState.ServiceId;

    internal void Enqueue(ExtensionServiceLogEntry entry)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _completed) != 0)
        {
            return;
        }

        if (entry.Sequence is { } sequence)
        {
            SetHighestSequence(sequence);
        }

        _channel.Writer.TryWrite(entry);
    }

    internal void CompleteNormally()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
    }

    internal void TerminateForExtensionUnload() =>
        _owner.TerminateSubscription(this, ExtensionServiceLogTerminationReason.ExtensionUnloaded);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        MarkDetached();
        _channel.Writer.TryComplete();
        try
        {
            _stopSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        lock (_startGate)
        {
            if (_deliveryTask is null)
            {
                _stopSource.Dispose();
                MarkQuiesced();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await DeliveryTask.ConfigureAwait(false);
    }

    private async Task DeliverAsync()
    {
        try
        {
            DeliverEntry(_initialState);
            for (var index = 0; index < _replayEntries.Count; index++)
            {
                _stopSource.Token.ThrowIfCancellationRequested();
                DeliverEntry(_replayEntries[index]);
            }

            await foreach (var entry in _channel.Reader.ReadAllAsync(_stopSource.Token).ConfigureAwait(false))
            {
                DeliverEntry(entry);
            }

            if (Volatile.Read(ref _disposed) == 0)
            {
                var highestSequence = Volatile.Read(ref _highestSequenceQueued);
                if (highestSequence > _lastDeliveredSequence)
                {
                    DeliverGap(_lastDeliveredSequence + 1, highestSequence);
                }

                InvokeSink(_sink.OnCompleted, nameof(IExtensionServiceLogSink.OnCompleted));
            }
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
        {
        }
        finally
        {
            MarkDetached();
            MarkQuiesced();
            _stopSource.Dispose();
        }
    }

    private void DeliverEntry(ExtensionServiceLogEntry entry)
    {
        if (entry.Kind == ExtensionServiceLogEntryKind.Gap)
        {
            InvokeSink(() => _sink.OnEntry(entry), nameof(IExtensionServiceLogSink.OnEntry));
            if (entry.LastMissingSequence is { } missingThrough)
            {
                _lastDeliveredSequence = Math.Max(_lastDeliveredSequence, missingThrough);
            }

            return;
        }

        if (entry.Sequence is { } sequence)
        {
            if (sequence > _lastDeliveredSequence + 1)
            {
                DeliverGap(_lastDeliveredSequence + 1, sequence - 1);
            }

            _lastDeliveredSequence = Math.Max(_lastDeliveredSequence, sequence);
        }

        InvokeSink(() => _sink.OnEntry(entry), nameof(IExtensionServiceLogSink.OnEntry));
    }

    private void DeliverGap(long firstMissingSequence, long lastMissingSequence)
    {
        if (firstMissingSequence > lastMissingSequence)
        {
            return;
        }

        var gap = new ExtensionServiceLogEntry(
            ExtensionServiceLogEntryKind.Gap,
            ServiceId,
            lastMissingSequence,
            DateTimeOffset.UtcNow,
            firstMissingSequence: firstMissingSequence,
            lastMissingSequence: lastMissingSequence);
        _lastDeliveredSequence = Math.Max(_lastDeliveredSequence, lastMissingSequence);
        InvokeSink(() => _sink.OnEntry(gap), nameof(IExtensionServiceLogSink.OnEntry));
    }

    private void SetHighestSequence(long sequence)
    {
        var current = Volatile.Read(ref _highestSequenceQueued);
        while (sequence > current)
        {
            var observed = Interlocked.CompareExchange(ref _highestSequenceQueued, sequence, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static void InvokeSink(Action callback, string operation)
    {
        try
        {
            using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.ServiceLog))
            {
                callback();
            }
        }
        catch
        {
            _ = operation;
        }
    }

    private void MarkDetached()
    {
        if (Interlocked.Exchange(ref _detached, 1) == 0)
        {
            _owner.RemoveSubscription(this);
            _onDetached(this);
        }
    }

    private void MarkQuiesced()
    {
        if (Interlocked.Exchange(ref _quiesced, 1) == 0)
        {
            _onQuiesced(this);
        }
    }
}
