using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Owns one bounded ordered best-effort event queue for an extension generation.</summary>
internal sealed class ExtensionEventQueue : IExtensionEventPublisher, IAsyncDisposable
{
    private const int DefaultCapacity = 1024;
    private readonly object _gate = new();
    private readonly Queue<ExtensionEvent> _events = new();
    private readonly List<Func<ExtensionEvent, CancellationToken, ValueTask>> _subscribers = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<Exception, ValueTask> _onFailure;
    private readonly Func<long, ValueTask>? _onDrop;
    private readonly int _capacity;
    private readonly Task _consumer;
    private long _dropped;
    private bool _stopped;

    internal ExtensionEventQueue(
        Func<Exception, ValueTask> onFailure,
        int capacity = DefaultCapacity,
        Func<long, ValueTask>? onDrop = null)
    {
        _onFailure = onFailure;
        _onDrop = onDrop;
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

        var dropped = false;
        lock (_gate)
        {
            if (_stopped || _events.Count >= _capacity)
            {
                Interlocked.Increment(ref _dropped);
                dropped = true;
            }
            else
            {
                _events.Enqueue(@event);
                _available.Release();
            }
        }

        if (dropped)
        {
            if (_onDrop is { } onDrop)
            {
                _ = ObserveDropAsync(onDrop(DroppedCount).AsTask());
            }

            return false;
        }

        return true;
    }

    private static async Task ObserveDropAsync(Task notification)
    {
        try
        {
            await notification.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
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
                    using var callbackScope = ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Event);
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
