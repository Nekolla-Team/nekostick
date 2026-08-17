using System.Collections.Concurrent;
using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

internal sealed class ExtensionHandlerRegistry : IExtensionRegistration
{
    private readonly Dictionary<string, IExtensionHandler> _handlers = new(StringComparer.Ordinal);
    private IExtensionFallback? _fallback;

    internal bool RegistrationRejected { get; private set; }

    internal IReadOnlyDictionary<string, IExtensionHandler> Handlers => _handlers;

    internal IExtensionFallback? Fallback => _fallback;

    public bool TryRegisterHandler(IExtensionHandler handler)
    {
        if (handler is null || !ExtensionIdentifierSyntax.IsValid(handler.HandlerId))
        {
            return false;
        }

        if (_handlers.TryGetValue(handler.HandlerId, out var existing))
        {
            if (ReferenceEquals(existing, handler))
            {
                return true;
            }

            RegistrationRejected = true;
            return false;
        }

        _handlers.Add(handler.HandlerId, handler);
        return true;
    }


    public bool TryRegisterFallback(IExtensionFallback fallback)
    {
        if (fallback is null)
        {
            return false;
        }

        if (_fallback is not null)
        {
            if (ReferenceEquals(_fallback, fallback))
            {
                return true;
            }

            RegistrationRejected = true;
            return false;
        }

        _fallback = fallback;
        return true;
    }

    internal void Clear()
    {
        _handlers.Clear();
        _fallback = null;
    }
}

internal sealed class ExtensionSettingsReader : IExtensionSettingsReader
{
    internal ExtensionSettingsReader(ExtensionSettingsConfiguration? settings)
    {
        Settings = settings;
    }

    public ExtensionSettingsConfiguration? Settings { get; }
}

internal sealed class ExtensionStatusSink : IExtensionStatusSink
{
    private readonly Action<ExtensionStatus> _report;

    internal ExtensionStatusSink(Action<ExtensionStatus> report)
    {
        _report = report;
    }

    public void Report(ExtensionStatus status) => _report(status);
}

internal sealed class ExtensionLogger : IExtensionLogger
{
    private readonly Action<ExtensionLogLevel, string> _report;

    internal ExtensionLogger(Action<ExtensionLogLevel, string> report)
    {
        _report = report;
    }

    public void Report(ExtensionLogLevel level, string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128)
        {
            return;
        }

        _report(level, code);
    }
}

internal sealed class ExtensionEventQueue : IExtensionEventPublisher, IAsyncDisposable
{
    private const int DefaultCapacity = 1024;
    private readonly object _gate = new();
    private readonly Queue<ExtensionEvent> _events = new();
    private readonly List<Func<ExtensionEvent, CancellationToken, ValueTask>> _subscribers = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<Exception, ValueTask> _onFailure;
    private readonly int _capacity;
    private readonly Task _consumer;
    private long _dropped;
    private bool _stopped;

    internal ExtensionEventQueue(Func<Exception, ValueTask> onFailure, int capacity = DefaultCapacity)
    {
        _onFailure = onFailure;
        _capacity = capacity is < 1 or > 1024 ? DefaultCapacity : capacity;
        _consumer = ConsumeAsync();
    }

    internal long DroppedCount => Interlocked.Read(ref _dropped);

    public bool TryPublish(ExtensionEvent @event)
    {
        if (@event is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_stopped || _events.Count >= _capacity)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            _events.Enqueue(@event);
            _available.Release();
        }

        return true;
    }

    public bool TrySubscribe(Func<ExtensionEvent, CancellationToken, ValueTask> callback)
    {
        if (callback is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_stopped)
            {
                return false;
            }

            _subscribers.Add(callback);
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }

        _stop.Cancel();
        try
        {
            await _consumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_gate)
            {
                _events.Clear();
                _subscribers.Clear();
            }

            _stop.Dispose();
            _available.Dispose();
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(_stop.Token).ConfigureAwait(false);
                ExtensionEvent? @event;
                Func<ExtensionEvent, CancellationToken, ValueTask>[] subscribers;
                lock (_gate)
                {
                    @event = _events.Count == 0 ? null : _events.Dequeue();
                    subscribers = _subscribers.ToArray();
                }

                if (@event is null)
                {
                    continue;
                }

                foreach (var subscriber in subscribers)
                {
                    try
                    {
                        await subscriber(@event, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            await _onFailure(exception).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }
}

internal sealed class ExtensionTaskTracker : IExtensionTaskScheduler, IDisposable
{
    private const int MaxTasks = 64;
    private readonly object _gate = new();
    private readonly HashSet<Task> _tasks = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<Exception, ValueTask> _onFailure;
    private bool _stopped;
    private bool _disposed;

    internal ExtensionTaskTracker(Func<Exception, ValueTask> onFailure)
    {
        _onFailure = onFailure;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _tasks.Count;
            }
        }
    }

    public ValueTask<bool> StartAsync(string taskName, Func<CancellationToken, ValueTask> callback)
    {
        if (string.IsNullOrWhiteSpace(taskName) || taskName.Length > 128 || callback is null)
        {
            return ValueTask.FromResult(false);
        }

        lock (_gate)
        {
            if (_stopped || _tasks.Count >= MaxTasks)
            {
                return ValueTask.FromResult(false);
            }

            var task = RunTrackedAsync(callback);
            _tasks.Add(task);
            _ = task.ContinueWith(
                completed =>
                {
                    lock (_gate)
                    {
                        _tasks.Remove(completed);
                    }

                    DisposeStopSourceIfIdle();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return ValueTask.FromResult(true);
        }
    }

    internal async ValueTask StopAsync(TimeSpan timeout)
    {
        Task[] tasks;
        lock (_gate)
        {
            if (_stopped)
            {
                tasks = _tasks.ToArray();
            }
            else
            {
                _stopped = true;
                _stop.Cancel();
                tasks = _tasks.ToArray();
            }
        }

        if (tasks.Length == 0)
        {
            DisposeStopSourceIfIdle();
            return;
        }

        try
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        DisposeStopSourceIfIdle();

        return;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_stopped)
            {
                _stopped = true;
                _stop.Cancel();
            }
        }

        DisposeStopSourceIfIdle();
    }

    private void DisposeStopSourceIfIdle()
    {
        lock (_gate)
        {
            if (_stopped && _tasks.Count == 0 && !_disposed)
            {
                _disposed = true;
                _stop.Dispose();
            }
        }
    }

    private async Task RunTrackedAsync(Func<CancellationToken, ValueTask> callback)
    {
        try
        {
            await callback(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                await _onFailure(exception).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }
}

internal sealed class ExtensionFailureTracker
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private readonly int _threshold;
    private readonly TimeSpan _window;

    internal ExtensionFailureTracker(int threshold = 10, TimeSpan? window = null)
    {
        _threshold = threshold < 1 ? 10 : threshold;
        _window = window is null || window <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : window.Value;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                RemoveExpired(DateTimeOffset.UtcNow);
                return _failures.Count;
            }
        }
    }

    internal bool Record(DateTimeOffset now)
    {
        lock (_gate)
        {
            RemoveExpired(now);
            _failures.Enqueue(now);
            return _failures.Count >= _threshold;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        while (_failures.Count > 0 && now - _failures.Peek() > _window)
        {
            _failures.Dequeue();
        }
    }
}
